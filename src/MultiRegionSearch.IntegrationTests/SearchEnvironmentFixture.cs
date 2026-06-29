using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;

/// <summary>
/// Shared fixture for integration tests. Creates a dedicated "products-it" index in every
/// configured region, seeds SampleData.Products, waits for the documents to commit, then
/// deletes the index on dispose so tests never touch the real "products" index.
/// </summary>
public sealed class SearchEnvironmentFixture : IAsyncLifetime
{
    /// <summary>Config loaded from appsettings.json with IndexName overridden to "products-it".</summary>
    public SearchConfig Settings { get; }

    /// <summary>Credential used for all SDK calls.</summary>
    public TokenCredential Credential { get; }

    /// <summary>
    /// True when appsettings.json contains at least one region with a real (non-placeholder) endpoint.
    /// Tests skip themselves when this is false.
    /// </summary>
    public bool IsConfigured { get; }

    public SearchEnvironmentFixture()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var raw = config.GetSection("Search").Get<SearchConfig>() ?? new SearchConfig();
        raw.IndexName = "products-it"; // dedicated test index — never touches production data
        Settings = raw;

        Credential = new DefaultAzureCredential();

        IsConfigured = Settings.Regions.Count >= 1 &&
            Settings.Regions.All(r =>
                !string.IsNullOrWhiteSpace(r.Endpoint) &&
                !r.Endpoint.Contains('<') &&
                !r.Endpoint.Contains("replace", StringComparison.OrdinalIgnoreCase));
    }

    public async Task InitializeAsync()
    {
        if (!IsConfigured) return;

        // --- 1. Create / update the test index schema in every region ---
        var index = new SearchIndex(Settings.IndexName, new FieldBuilder().Build(typeof(Product)));
        await Task.WhenAll(Settings.Regions.Select(async region =>
        {
            var client = new SearchIndexClient(new Uri(region.Endpoint), Credential);
            await client.CreateOrUpdateIndexAsync(index);
        }));

        // --- 2. Seed documents to every region ---
        var batch = IndexDocumentsBatch.MergeOrUpload(SampleData.Products);
        await Task.WhenAll(Settings.Regions.Select(async region =>
        {
            var client = new SearchClient(new Uri(region.Endpoint), Settings.IndexName, Credential);
            var result = await client.IndexDocumentsAsync(batch);
            var failed = result.Value.Results.Where(r => !r.Succeeded).ToList();
            if (failed.Count > 0)
                throw new InvalidOperationException(
                    $"[fixture] {region.Name}: {failed.Count} document(s) failed to seed. " +
                    string.Join(", ", failed.Select(f => $"{f.Key}: {f.ErrorMessage}")));
        }));

        // --- 3. Poll until all regions have committed the expected document count (~30s max) ---
        // POST /docs/search?count=true is authoritative within ~1s of the indexing op.
        var expected = SampleData.Products.Count;
        var pending = new HashSet<string>(Settings.Regions.Select(r => r.Name));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (pending.Count > 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            foreach (var region in Settings.Regions.Where(r => pending.Contains(r.Name)).ToList())
            {
                var client = new SearchClient(new Uri(region.Endpoint), Settings.IndexName, Credential);
                var resp = await client.SearchAsync<Product>(
                    "*", new SearchOptions { Size = 0, IncludeTotalCount = true });
                if (resp.Value.TotalCount >= expected)
                    pending.Remove(region.Name);
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (!IsConfigured) return;

        await Task.WhenAll(Settings.Regions.Select(async region =>
        {
            try
            {
                var client = new SearchIndexClient(new Uri(region.Endpoint), Credential);
                await client.DeleteIndexAsync(Settings.IndexName);
            }
            catch
            {
                // Best-effort cleanup; ignore errors (e.g. index already deleted).
            }
        }));
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
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };
            options.Transport = new HttpClientTransport(new HttpClient(handler));
        }
        return new SearchClient(new Uri(Settings.Gateway.Url), Settings.IndexName, Credential, options);
    }
}
