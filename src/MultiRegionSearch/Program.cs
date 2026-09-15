using System.Diagnostics;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var settings = config.GetSection("Search").Get<SearchConfig>()
    ?? throw new InvalidOperationException("Missing 'Search' section in appsettings.json.");

// One credential, used for both the gateway read path and the direct write path.
// RBAC means the same bearer token is accepted by every regional search service.
TokenCredential credential = new DefaultAzureCredential();

var command = (args.Length > 0 ? args[0] : "help").ToLowerInvariant();

switch (command)
{
    case "init":
        await InitAsync();
        break;
    case "seed":
        await SeedAsync();
        break;
    case "query":
        await QueryViaGatewayAsync(args.Length > 1 ? args[1] : "*");
        break;
    case "query-direct":
        await QueryDirectAsync(args.ElementAtOrDefault(1), args.ElementAtOrDefault(2) ?? "*");
        break;
    case "status":
        await StatusAsync();
        break;
    case "sync-check":
        await SyncCheckAsync();
        break;
    case "bench":
        await BenchAsync(
            int.TryParse(args.ElementAtOrDefault(1), out var n) ? n : 50,
            int.TryParse(args.ElementAtOrDefault(2), out var p) ? p : 4);
        break;
    case "demo":
        await InitAsync();
        await SeedAsync();
        Console.WriteLine("Waiting a moment for documents to commit...");
        await Task.Delay(2000);
        await QueryViaGatewayAsync("wireless");
        await StatusAsync();
        break;
    default:
        PrintHelp();
        break;
}

// --- Commands ---------------------------------------------------------------

// Create/update the index schema in every region (control plane, direct to each service).
async Task InitAsync()
{
    var index = new SearchIndex(settings.IndexName, new FieldBuilder().Build(typeof(Product)));
    var tasks = settings.Regions.Select(async region =>
    {
        var client = new SearchIndexClient(new Uri(region.Endpoint), credential);
        await client.CreateOrUpdateIndexAsync(index);
        Console.WriteLine($"[init] index '{settings.IndexName}' ready in {region.Name}");
    });
    await Task.WhenAll(tasks);
}

// Fan out the same documents to every region in parallel.
// A load balancer delivers each request to ONE backend, so writes must be replicated by the
// client. mergeOrUpload makes the fan-out idempotent and safe to retry per region.
async Task SeedAsync()
{
    var docs = SampleData.Products;
    var batch = IndexDocumentsBatch.MergeOrUpload(docs);

    var tasks = settings.Regions.Select(async region =>
    {
        var client = new SearchClient(new Uri(region.Endpoint), settings.IndexName, credential);
        var response = await client.IndexDocumentsAsync(batch);
        var results = response.Value.Results;
        var succeeded = results.Count(r => r.Succeeded);
        Console.WriteLine($"[seed] {region.Name}: {succeeded}/{docs.Count} documents merged");

        var failed = results.Where(r => !r.Succeeded).ToList();
        if (failed.Count > 0)
        {
            foreach (var f in failed)
                Console.Error.WriteLine($"[seed] {region.Name}: FAILED key='{f.Key}' status={f.Status} error={f.ErrorMessage}");
            throw new InvalidOperationException(
                $"[seed] {region.Name}: {failed.Count} document(s) failed to index.");
        }
    });

    await Task.WhenAll(tasks);
}

// Query through the Application Gateway: load-balanced across regions, with automatic
// failover when a region's health probe fails.
async Task QueryViaGatewayAsync(string text)
{
    var client = CreateGatewayClient();
    var sw = Stopwatch.StartNew();
    var response = await client.SearchAsync<Product>(text, new SearchOptions { Size = 5, IncludeTotalCount = true });
    sw.Stop();

    Console.WriteLine($"[query @ gateway] '{text}' -> {response.Value.TotalCount} hits in {sw.ElapsedMilliseconds} ms");
    await foreach (var result in response.Value.GetResultsAsync())
    {
        Console.WriteLine($"   {result.Score:F2}  {result.Document.Name}  (${result.Document.Price})  [{result.Document.Category}]");
    }
}

// Query a single region directly, bypassing the gateway (useful to prove the regions are in sync).
async Task QueryDirectAsync(string? regionName, string text)
{
    var region = settings.Regions.FirstOrDefault(r => string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase));
    if (region is null)
    {
        Console.WriteLine($"Unknown region '{regionName}'. Known regions: {string.Join(", ", settings.Regions.Select(r => r.Name))}");
        return;
    }

    var client = new SearchClient(new Uri(region.Endpoint), settings.IndexName, credential);
    var response = await client.SearchAsync<Product>(text, new SearchOptions { Size = 5, IncludeTotalCount = true });

    Console.WriteLine($"[query @ {region.Name}] '{text}' -> {response.Value.TotalCount} hits");
    await foreach (var result in response.Value.GetResultsAsync())
    {
        Console.WriteLine($"   {result.Score:F2}  {result.Document.Name}");
    }
}

// Document count per region. /docs/search with count=true is authoritative within ~1s of indexing
// (the /stats document counter lags and should not be used as a freshness signal).
async Task StatusAsync()
{
    var tasks = settings.Regions.Select(async region =>
    {
        var client = new SearchClient(new Uri(region.Endpoint), settings.IndexName, credential);
        try
        {
            var response = await client.SearchAsync<Product>("*", new SearchOptions { Size = 0, IncludeTotalCount = true });
            Console.WriteLine($"[status] {region.Name}: {response.Value.TotalCount} documents");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[status] {region.Name}: ERROR {ex.Message}");
        }
    });
    await Task.WhenAll(tasks);
}

// Fetch all documents from every region and compare them for drift.
// Reports any missing documents or field-level differences between regions.
async Task SyncCheckAsync()
{
    Console.WriteLine("[sync-check] fetching all documents from each region...");

    // Retrieve all docs from each region keyed by Id.
    var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>();

    foreach (var region in settings.Regions)
    {
        var client = new SearchClient(new Uri(region.Endpoint), settings.IndexName, credential);
        var docs = new Dictionary<string, Product>();
        try
        {
            int skip = 0;
            const int pageSize = 1000;
            while (true)
            {
                var response = await client.SearchAsync<Product>("*",
                    new SearchOptions { Size = pageSize, Skip = skip, IncludeTotalCount = true });
                int count = 0;
                await foreach (var result in response.Value.GetResultsAsync())
                {
                    docs[result.Document.Id] = result.Document;
                    count++;
                }
                if (count < pageSize) break;
                skip += pageSize;
            }

            Console.WriteLine($"[sync-check] {region.Name}: {docs.Count} documents");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[sync-check] {region.Name}: ERROR fetching documents — {ex.Message}");
        }
        perRegion[region.Name] = docs;
    }

    if (perRegion.Count < 2)
    {
        Console.WriteLine("[sync-check] need at least 2 regions to compare. Check appsettings.json.");
        return;
    }

    var syncResult = SyncAnalyzer.Analyze(perRegion);

    foreach (var issue in syncResult.Issues)
    {
        if (issue.Kind == SyncIssueKind.Missing)
            Console.WriteLine($"[sync-check] MISSING  doc '{issue.DocumentId}' absent in: {issue.Region}");
        else
            Console.WriteLine($"[sync-check] DRIFT    doc '{issue.DocumentId}' in {issue.Region}: {issue.Detail}");
    }

    var allDocIds = perRegion.Values.SelectMany(d => d.Keys).Distinct().ToList();
    Console.WriteLine(syncResult.InSync
        ? $"[sync-check] OK — all {allDocIds.Count} documents are identical across {string.Join(", ", perRegion.Keys)}."
        : "[sync-check] FAIL — drift detected (see above).");
}

// Fire N concurrent queries through the gateway and report success rate + latency.
// Take a region offline (see README) and re-run to watch the gateway fail over with zero errors.
async Task BenchAsync(int count, int parallelism = 4)
{
    var client = CreateGatewayClient();
    string[] terms = { "wireless", "coffee", "laptop", "chair", "bottle", "*" };
    int ok = 0, fail = 0;
    var latencies = new System.Collections.Concurrent.ConcurrentBag<long>();

    Console.WriteLine($"[bench] {count} requests (parallelism={parallelism}) via gateway...");

    await Parallel.ForEachAsync(
        Enumerable.Range(0, count),
        new ParallelOptions { MaxDegreeOfParallelism = parallelism },
        async (i, ct) =>
        {
            var term = terms[i % terms.Length];
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await client.SearchAsync<Product>(term, new SearchOptions { Size = 1 }, ct);
                _ = response.Value.TotalCount;
                sw.Stop();
                latencies.Add(sw.ElapsedMilliseconds);
                Interlocked.Increment(ref ok);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Interlocked.Increment(ref fail);
                Console.WriteLine($"   request {i} ({term}) failed: {ex.Message}");
            }
        });

    Console.WriteLine($"[bench] {count} requests via gateway: {ok} ok, {fail} failed");
    var stats = LatencyCalculator.Compute(latencies.ToList());
    if (stats is not null)
        Console.WriteLine($"   latency ms: min={stats.Min} p50={stats.P50} p95={stats.P95} max={stats.Max}");
}

// --- Helpers ----------------------------------------------------------------

SearchClient CreateGatewayClient()
{
    if (!settings.Gateway.IsConfigured)
        throw new InvalidOperationException("Gateway URL is not configured. Run deploy.ps1 or edit appsettings.json.");

    var options = new SearchClientOptions();
    if (settings.Gateway.AllowSelfSignedCert)
    {
        // Demo only: the gateway uses a self-signed certificate. Production should use a trusted cert.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        options.Transport = new HttpClientTransport(new HttpClient(handler));
    }

    return new SearchClient(new Uri(settings.Gateway.Url), settings.IndexName, credential, options);
}

void PrintHelp()
{
    Console.WriteLine("""
        MultiRegionSearch - consume Azure AI Search across regions via Application Gateway

        Usage: dotnet run -- <command> [args]

          init                       Create the index schema in every region
          seed                       Fan out sample documents to every region (mergeOrUpload)
          query <text>               Query through the Application Gateway (load-balanced + failover)
          query-direct <region> <t>  Query one region directly (bypass the gateway)
          status                     Show document count per region
          sync-check                 Fetch all docs from every region and compare for drift
          bench <count> [concurrency]  Fire N concurrent queries through the gateway; report success + latency (default concurrency=4)
          demo                       init + seed + a gateway query + status

        Auth: uses DefaultAzureCredential (run 'az login'). All access is via Microsoft Entra RBAC.
        """);
}
