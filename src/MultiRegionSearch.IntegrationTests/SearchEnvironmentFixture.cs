using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Shared fixture for integration tests. Creates a unique "products-it-{guid}" index in every
/// configured region, seeds SampleData.Products, waits for the documents to commit, then
/// deletes the index on dispose so tests never touch the real "products" index.
/// </summary>
public sealed class SearchEnvironmentFixture : IAsyncLifetime
{
    private readonly HashSet<RegionConfig> cleanupRegions = new();
    private readonly string journalDirectory;
    private HttpClient? gatewayHttpClient;

    /// <summary>Config loaded from appsettings.json with an isolated index and journal per run.</summary>
    public SearchConfig Settings { get; }

    /// <summary>Credential used for all SDK calls.</summary>
    public TokenCredential Credential { get; }

    /// <summary>
    /// True when appsettings.json contains at least one region with a real (non-placeholder) endpoint.
    /// Tests skip themselves when this is false.
    /// </summary>
    public bool IsConfigured { get; }

    public SearchEnvironmentFixture() : this(new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddEnvironmentVariables()
        .Build())
    {
    }

    internal SearchEnvironmentFixture(IConfiguration config)
    {
        var raw = config.GetSection("Search").Get<SearchConfig>() ?? new SearchConfig();
        raw.IndexName = $"products-it-{Guid.NewGuid():N}";
        journalDirectory = Path.Combine(AppContext.BaseDirectory, "integration-artifacts", raw.IndexName);
        raw.ReplicationJournalPath = Path.Combine(journalDirectory, "replication-journal.json");
        Settings = raw;

        Credential = new DefaultAzureCredential();

        IsConfigured = Settings.Regions.Count >= 1 &&
            Settings.Regions.All(r => GatewayConfig.IsValidEndpoint(r.Endpoint) &&
                new Uri(r.Endpoint).Host.EndsWith(".search.windows.net", StringComparison.OrdinalIgnoreCase));

        var required = string.Equals(config["AZURE_INTEGRATION_REQUIRED"], "true", StringComparison.OrdinalIgnoreCase);
        if (required && (!IsConfigured || Settings.Regions.Count < 2 || !Settings.Gateway.IsConfigured))
            throw new InvalidOperationException(
                "Integration is required: configure two distinct real Search endpoints and an HTTPS gateway URL.");
        if (IsConfigured)
            Settings.Validate(requireGateway: required);
        else if (Settings.Regions.Any(r => GatewayConfig.IsValidEndpoint(r.Endpoint)))
            throw new InvalidOperationException("Partially configured integration regions are not allowed.");
    }

    public async Task InitializeAsync()
    {
        if (!IsConfigured) return;

        // Register all attempted regions before creation: an interrupted request may still succeed.
        cleanupRegions.UnionWith(Settings.Regions);
        try
        {
            foreach (var command in new[] { "init", "seed" })
            {
                var result = await RunCommandAsync(command);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException(
                        $"[fixture] {command} failed with exit code {result.ExitCode}:\n{result.Error}\n{result.Output}");
            }

            await WaitForExactReadinessAsync();
        }
        catch (Exception initializationError)
        {
            try { await DisposeAsync(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Integration initialization and cleanup failed.", initializationError, cleanupError);
            }
            throw;
        }
    }

    public async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(params string[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var exitCode = await CliApplication.RunAsync(arguments, loadConfig: _ => Settings,
            output: output, error: error, cancellationToken: timeout.Token);
        return (exitCode, output.ToString(), error.ToString());
    }

    private async Task WaitForExactReadinessAsync()
    {
        var pending = Settings.Regions.ToDictionary(r => r.Name, _ => "not queried");
        var expected = SampleData.Products.ToDictionary(p => p.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            while (pending.Count > 0)
            {
                foreach (var region in Settings.Regions.Where(r => pending.ContainsKey(r.Name)))
                {
                    try
                    {
                        var client = new SearchClient(new Uri(region.Endpoint), Settings.IndexName, Credential);
                        var response = await client.SearchAsync<Product>("*",
                            new SearchOptions { Size = 1000, IncludeTotalCount = true }, timeout.Token);
                        var actual = new Dictionary<string, Product>();
                        await foreach (var item in response.Value.GetResultsAsync().WithCancellation(timeout.Token))
                            actual.Add(item.Document.Id, item.Document);
                        var comparison = SyncAnalyzer.Analyze(new Dictionary<string, IReadOnlyDictionary<string, Product>>
                        {
                            ["expected"] = expected,
                            ["actual"] = actual
                        });
                        if (response.Value.TotalCount == expected.Count && actual.Count == expected.Count && comparison.InSync)
                            pending.Remove(region.Name);
                        else
                            pending[region.Name] = $"count={response.Value.TotalCount}, content differences={comparison.Issues.Count}";
                    }
                    catch (Azure.RequestFailedException ex) when (ex.Status is 404 or 408 or 429 || ex.Status >= 500)
                    {
                        pending[region.Name] = $"HTTP {ex.Status}";
                    }
                }
                if (pending.Count > 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Exact integration document readiness timed out after 60 seconds: " +
                string.Join("; ", pending.Select(p => $"{p.Key}: {p.Value}")));
        }
    }

    public async Task DisposeAsync()
    {
        var errors = new List<Exception>();
        foreach (var region in cleanupRegions.ToArray())
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var client = new SearchIndexClient(new Uri(region.Endpoint), Credential);
                await client.DeleteIndexAsync(Settings.IndexName, cancellationToken: timeout.Token);
                cleanupRegions.Remove(region);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404) { cleanupRegions.Remove(region); }
            catch (Exception ex) { errors.Add(new InvalidOperationException($"Cleanup failed: {region.Name}/{Settings.IndexName}", ex)); }
        }
        gatewayHttpClient?.Dispose();
        gatewayHttpClient = null;
        try
        {
            // This run owns the unique path, and all command instances have released the lock.
            foreach (var suffix in new[] { "", ".new", ".lock" })
            {
                var path = Settings.ReplicationJournalPath + suffix;
                if (File.Exists(path))
                    File.Delete(path);
            }
            if (Directory.Exists(journalDirectory))
                Directory.Delete(journalDirectory);
        }
        catch (Exception ex) { errors.Add(ex); }
        if (errors.Count > 0)
            throw new AggregateException("Integration cleanup failed; remove the listed isolated indexes manually.", errors);
    }

    /// <summary>
    /// Builds a SearchClient for the Application Gateway. Returns null when the gateway
    /// URL is not configured so callers can skip gateway-specific tests.
    /// </summary>
    public SearchClient? TryCreateGatewayClient()
    {
        if (!Settings.Gateway.IsConfigured) return null;

        var options = new SearchClientOptions();
        if (Settings.Gateway.AllowSelfSignedCert)
        {
            gatewayHttpClient ??= new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            });
            options.Transport = new HttpClientTransport(gatewayHttpClient);
        }
        return new SearchClient(new Uri(Settings.Gateway.Url), Settings.IndexName, Credential, options);
    }
}
