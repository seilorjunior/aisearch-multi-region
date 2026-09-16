using System.Collections.Concurrent;
using System.Diagnostics;

public sealed class CommandRunner(SearchConfig settings, ISearchService service, CommandLog log)
{
    public async Task<int> RunAsync(CommandOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (options.Command)
        {
            case "init": return await InitializeAsync(cancellationToken) ? 0 : 1;
            case "seed": return await ReplicateAsync(false, cancellationToken) ? 0 : 1;
            case "replay": return await ReplicateAsync(true, cancellationToken) ? 0 : 1;
            case "query":
                await QueryAsync(null, options.Arguments.FirstOrDefault() ?? "*", cancellationToken);
                return 0;
            case "query-direct":
                var region = settings.Regions.Single(r => string.Equals(r.Name, options.Arguments[0], StringComparison.OrdinalIgnoreCase));
                await QueryAsync(region, options.Arguments.ElementAtOrDefault(1) ?? "*", cancellationToken);
                return 0;
            case "status": return await StatusAsync(cancellationToken) ? 0 : 1;
            case "sync-check": return await SyncCheckAsync(cancellationToken) ? 0 : 1;
            case "bench": return await BenchmarkAsync(
                options.Arguments.Length > 0 ? int.Parse(options.Arguments[0]) : 50,
                options.Arguments.Length > 1 ? int.Parse(options.Arguments[1]) : 4, cancellationToken) ? 0 : 1;
            case "demo":
                if (!await InitializeAsync(cancellationToken) || !await ReplicateAsync(false, cancellationToken) ||
                    !await WaitForReadinessAsync(cancellationToken)) return 1;
                await QueryAsync(null, "wireless", cancellationToken);
                return await StatusAsync(cancellationToken) ? 0 : 1;
            default: throw new ArgumentException("Unsupported command.");
        }
    }

    private async Task<bool> InitializeAsync(CancellationToken cancellationToken)
    {
        var success = true;
        foreach (var region in settings.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await service.InitializeAsync(region, cancellationToken);
                log.Write("init", $"{region.Name}: index '{settings.IndexName}' ready");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { success = false; log.Write("init", $"{region.Name}: {ex.Message}", failure: true); }
        }
        return success;
    }

    private async Task<bool> ReplicateAsync(bool replay, CancellationToken cancellationToken)
    {
        using var journal = new ReplicationJournal(settings);
        if (!replay) journal.Begin(settings, SampleData.Products);
        if (journal.Batch is null || journal.Batch.Complete)
        {
            log.Write("replay", "No pending replication.");
            return true;
        }
        var batch = journal.Batch;
        foreach (var region in settings.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = batch.Documents.Where(d => !batch.Acknowledged[region.Name].Contains(d.Id)).ToList();
            if (pending.Count == 0) continue;
            IReadOnlyList<DocumentWriteResult> results;
            try
            {
                results = await service.WriteAsync(region, pending, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                log.Write("replication", $"{region.Name}: unavailable; {pending.Count} documents remain pending: {ex.Message}", failure: true);
                continue;
            }
            // Missing/duplicate responses are ambiguous and must remain pending.
            foreach (var document in pending)
            {
                var matches = results.Where(r => r.Key == document.Id).ToList();
                if (matches.Count == 1 && matches[0].Succeeded)
                    batch.Acknowledged[region.Name].Add(document.Id);
                else
                    log.Write("replication", $"{region.Name}: key '{document.Id}' remains pending",
                        new { region = region.Name, documentId = document.Id, results = matches }, failure: true);
            }
            // Persist acknowledgments even if cancellation was requested after the response.
            journal.Save();
            log.Write("replication", $"batch {batch.Sequence}, {region.Name}: {batch.Acknowledged[region.Name].Count}/{batch.Documents.Count} acknowledged",
                new { batch.Sequence, region = region.Name, acknowledged = batch.Acknowledged[region.Name].Count, total = batch.Documents.Count });
        }
        cancellationToken.ThrowIfCancellationRequested();
        log.Write("replication", batch.Complete ? "All regions acknowledged." : "Pending writes retained. Run replay.", failure: !batch.Complete);
        return batch.Complete;
    }

    private async Task QueryAsync(RegionConfig? region, string text, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var result = await service.QueryAsync(region, text, 5, cancellationToken);
        log.Write("query", $"{region?.Name ?? "gateway"}: '{text}' -> {result.TotalCount} hits in {timer.ElapsedMilliseconds} ms",
            new { region = region?.Name ?? "gateway", text, result.TotalCount, elapsedMs = timer.ElapsedMilliseconds, result.Documents });
        foreach (var product in result.Documents)
            log.Write("document", $"{product.Name} (${product.Price}) [{product.Category}]", product);
    }

    private async Task<bool> StatusAsync(CancellationToken cancellationToken)
    {
        var success = true;
        foreach (var region in settings.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await service.QueryAsync(region, "*", 0, cancellationToken);
                if (response.TotalCount is null) throw new InvalidOperationException("Document count is unavailable.");
                log.Write("status", $"{region.Name}: {response.TotalCount} documents", new { region = region.Name, count = response.TotalCount });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { success = false; log.Write("status", $"{region.Name}: unavailable: {ex.Message}", failure: true); }
        }
        return success;
    }

    private async Task<bool> SyncCheckAsync(CancellationToken cancellationToken)
    {
        var available = new Dictionary<string, IReadOnlyDictionary<string, Product>>();
        var unavailable = new List<string>();
        foreach (var region in settings.Regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                available[region.Name] = await service.FetchAsync(region, cancellationToken);
                log.Write("sync-check", $"{region.Name}: {available[region.Name].Count} documents");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                unavailable.Add(region.Name);
                log.Write("sync-check", $"{region.Name}: UNAVAILABLE: {ex.Message}", failure: true);
            }
        }
        if (unavailable.Count > 0 || available.Count < 2)
        {
            log.Write("sync-check", "INCONCLUSIVE — cannot verify every configured region; unavailable regions are not empty.", new { unavailable }, failure: true);
            return false;
        }
        var result = SyncAnalyzer.Analyze(available);
        foreach (var issue in result.Issues)
            log.Write("sync-check", $"{issue.Kind}: '{issue.DocumentId}' in {issue.Region}: {issue.Detail}", issue, failure: true);
        log.Write("sync-check", result.InSync ? "OK — all regions are identical." : "FAIL — drift detected.", failure: !result.InSync);
        return result.InSync;
    }

    private async Task<bool> WaitForReadinessAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.ReadinessTimeoutSeconds));
        var expected = SampleData.Products.ToDictionary(d => d.Id);
        try
        {
            while (true)
            {
                var ready = true;
                foreach (var region in settings.Regions)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var actual = await service.FetchAsync(region, timeout.Token);
                        var relevant = actual.Where(p => expected.ContainsKey(p.Key)).ToDictionary();
                        ready &= SyncAnalyzer.Analyze(new Dictionary<string, IReadOnlyDictionary<string, Product>>
                        {
                            ["expected"] = expected, ["actual"] = relevant
                        }).InSync;
                    }
                    catch (OperationCanceledException) when (timeout.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        ready = false;
                        log.Write("readiness", $"{region.Name}: not ready: {ex.Message}");
                    }
                }
                if (ready) { log.Write("readiness", "Sample documents visible in all regions."); return true; }
                await Task.Delay(settings.ReadinessPollIntervalMilliseconds, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            log.Write("readiness", $"Timed out after {settings.ReadinessTimeoutSeconds}s waiting for replicated documents.", failure: true);
            return false;
        }
    }

    private async Task<bool> BenchmarkAsync(int count, int parallelism, CancellationToken cancellationToken)
    {
        var successes = new ConcurrentBag<long>();
        var failures = new ConcurrentBag<long>();
        string[] terms = ["wireless", "coffee", "laptop", "chair", "bottle", "*"];
        var totalTimer = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, count),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var timer = Stopwatch.StartNew();
                try
                {
                    await service.QueryAsync(null, terms[i % terms.Length], 1, ct);
                    successes.Add(timer.ElapsedMilliseconds);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failures.Add(timer.ElapsedMilliseconds);
                    log.Write("bench", $"request {i} failed: {ex.Message}", failure: true);
                }
            });
        totalTimer.Stop();
        var successLatency = LatencyCalculator.Compute(successes.ToArray());
        var failedLatency = LatencyCalculator.Compute(failures.ToArray());
        var seconds = Math.Max(totalTimer.Elapsed.TotalSeconds, double.Epsilon);
        var requestsPerSecond = count / seconds;
        var successfulRequestsPerSecond = successes.Count / seconds;
        log.Write("bench", $"{count} requests: {successes.Count} ok, {failures.Count} failed; throughput={requestsPerSecond:F2} requests/s, successful={successfulRequestsPerSecond:F2}/s",
            new { count, parallelism, succeeded = successes.Count, failed = failures.Count, elapsedMs = totalTimer.Elapsed.TotalMilliseconds,
                requestsPerSecond, successfulRequestsPerSecond, successLatency, failedLatency });
        log.Write("bench", $"successful latency ms: {successLatency?.ToString() ?? "n/a"}; failed latency ms: {failedLatency?.ToString() ?? "n/a"}");
        return failures.IsEmpty;
    }
}
