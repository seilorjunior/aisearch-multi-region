using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;

/// <summary>
/// Integration tests that exercise the full Azure AI Search data path — init, seed, direct
/// queries, sync-check, and (optionally) gateway queries — against real Azure endpoints.
///
/// Tests skip automatically when appsettings.json contains placeholder URLs or no regions.
/// The fixture creates and destroys a dedicated "products-it" index so the production
/// "products" index is never modified.
///
/// Run only this category with:
///   dotnet test --filter "Category=Integration"
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests : IClassFixture<SearchEnvironmentFixture>
{
    private readonly SearchEnvironmentFixture _env;

    public IntegrationTests(SearchEnvironmentFixture env) => _env = env;

    // ── Init ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Init_IndexExistsInAllRegions()
    {
        Skip.If(!_env.IsConfigured, "No real Azure Search endpoints in appsettings.json.");

        foreach (var region in _env.Settings.Regions)
        {
            var client = new SearchIndexClient(new Uri(region.Endpoint), _env.Credential);
            var response = await client.GetIndexAsync(_env.Settings.IndexName);
            Assert.Equal(_env.Settings.IndexName, response.Value.Name);
        }
    }

    [SkippableFact]
    public async Task Init_IndexHasExpectedFields()
    {
        Skip.If(!_env.IsConfigured, "No real Azure Search endpoints in appsettings.json.");

        var expectedFields = new[] { "id", "name", "description", "category", "price", "rating", "tags" };

        foreach (var region in _env.Settings.Regions)
        {
            var client = new SearchIndexClient(new Uri(region.Endpoint), _env.Credential);
            var response = await client.GetIndexAsync(_env.Settings.IndexName);
            var actualFields = response.Value.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var field in expectedFields)
                Assert.Contains(field, actualFields);
        }
    }

    // ── Seed ─────────────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Seed_EachRegionHasExpectedDocumentCount()
    {
        Skip.If(!_env.IsConfigured, "No real Azure Search endpoints in appsettings.json.");

        foreach (var region in _env.Settings.Regions)
        {
            var client = new SearchClient(new Uri(region.Endpoint), _env.Settings.IndexName, _env.Credential);
            var response = await client.SearchAsync<Product>(
                "*", new SearchOptions { Size = 0, IncludeTotalCount = true });

            Assert.True(
                (int)response.Value.TotalCount!.Value == SampleData.Products.Count,
                $"Region '{region.Name}': expected {SampleData.Products.Count} docs but got {response.Value.TotalCount}.");
        }
    }

    // ── Direct query ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task QueryDirect_KnownTermReturnsMatchingDocuments()
    {
        Skip.If(!_env.IsConfigured, "No real Azure Search endpoints in appsettings.json.");

        foreach (var region in _env.Settings.Regions)
        {
            var client = new SearchClient(new Uri(region.Endpoint), _env.Settings.IndexName, _env.Credential);
            var response = await client.SearchAsync<Product>(
                "wireless", new SearchOptions { Size = 10, IncludeTotalCount = true });

            Assert.True(
                response.Value.TotalCount > 0,
                $"Region '{region.Name}': expected at least one hit for 'wireless'.");

            await foreach (var result in response.Value.GetResultsAsync())
            {
                Assert.NotNull(result.Document.Name);
                Assert.NotNull(result.Document.Id);
            }
        }
    }

    [SkippableFact]
    public async Task QueryDirect_UnknownTermReturnsZeroResults()
    {
        Skip.If(!_env.IsConfigured, "No real Azure Search endpoints in appsettings.json.");

        foreach (var region in _env.Settings.Regions)
        {
            var client = new SearchClient(new Uri(region.Endpoint), _env.Settings.IndexName, _env.Credential);
            var response = await client.SearchAsync<Product>(
                "xyzzy_no_such_product_42", new SearchOptions { Size = 10, IncludeTotalCount = true });

            Assert.Equal(0, (int)response.Value.TotalCount!.Value);
        }
    }

    [SkippableFact]
    public async Task QueryDirect_AllRegionsReturnSameCountForWildcard()
    {
        Skip.If(!_env.IsConfigured || _env.Settings.Regions.Count < 2,
            "Need at least 2 configured regions.");

        var counts = new List<(string Region, long Count)>();

        foreach (var region in _env.Settings.Regions)
        {
            var client = new SearchClient(new Uri(region.Endpoint), _env.Settings.IndexName, _env.Credential);
            var response = await client.SearchAsync<Product>(
                "*", new SearchOptions { Size = 0, IncludeTotalCount = true });
            counts.Add((region.Name, response.Value.TotalCount!.Value));
        }

        var expected = counts[0].Count;
        foreach (var (name, count) in counts)
            Assert.True(count == expected,
                $"Region '{name}' count {count} differs from '{counts[0].Region}' count {expected}.");
    }

    // ── Status (document count per region) ───────────────────────────────────

    [SkippableFact]
    public async Task Status_AllRegionsReportExpectedDocumentCount()
    {
        Skip.If(!_env.IsConfigured, "No real Azure Search endpoints in appsettings.json.");

        foreach (var region in _env.Settings.Regions)
        {
            var client = new SearchClient(new Uri(region.Endpoint), _env.Settings.IndexName, _env.Credential);
            var response = await client.SearchAsync<Product>(
                "*", new SearchOptions { Size = 0, IncludeTotalCount = true });

            Assert.True(
                (int)response.Value.TotalCount!.Value == SampleData.Products.Count,
                $"Status check for region '{region.Name}': expected {SampleData.Products.Count} docs but got {response.Value.TotalCount}.");
        }
    }

    // ── Sync-check ───────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task SyncCheck_AllRegionsAreInSync()
    {
        Skip.If(!_env.IsConfigured || _env.Settings.Regions.Count < 2,
            "Need at least 2 configured regions.");

        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>();

        foreach (var region in _env.Settings.Regions)
        {
            var client = new SearchClient(new Uri(region.Endpoint), _env.Settings.IndexName, _env.Credential);
            var docs = new Dictionary<string, Product>();
            var response = await client.SearchAsync<Product>(
                "*", new SearchOptions { Size = 1000, IncludeTotalCount = true });
            await foreach (var result in response.Value.GetResultsAsync())
                docs[result.Document.Id] = result.Document;
            perRegion[region.Name] = docs;
        }

        var syncResult = SyncAnalyzer.Analyze(perRegion);

        Assert.True(
            syncResult.InSync,
            "Regions are out of sync:\n" +
            string.Join("\n", syncResult.Issues.Select(i =>
                $"  [{i.Kind}] doc '{i.DocumentId}' in '{i.Region}': {i.Detail}")));
    }

    [SkippableFact]
    public async Task SyncCheck_EachRegionContainsEveryExpectedDocumentId()
    {
        Skip.If(!_env.IsConfigured, "No real Azure Search endpoints in appsettings.json.");

        var expectedIds = SampleData.Products.Select(p => p.Id).ToHashSet();

        foreach (var region in _env.Settings.Regions)
        {
            var client = new SearchClient(new Uri(region.Endpoint), _env.Settings.IndexName, _env.Credential);
            var response = await client.SearchAsync<Product>(
                "*", new SearchOptions { Size = 1000, IncludeTotalCount = true });

            var actualIds = new HashSet<string>();
            await foreach (var result in response.Value.GetResultsAsync())
                actualIds.Add(result.Document.Id);

            foreach (var id in expectedIds)
                Assert.True(actualIds.Contains(id), $"Region '{region.Name}' is missing document id '{id}'.");
        }
    }

    // ── Gateway query (skipped when gateway URL is a placeholder) ────────────

    [SkippableFact]
    public async Task QueryViaGateway_KnownTermReturnsResults()
    {
        Skip.If(!_env.IsConfigured, "No real Azure Search endpoints in appsettings.json.");
        var gatewayClient = _env.TryCreateGatewayClient();
        Skip.If(gatewayClient is null, "Gateway URL not configured in appsettings.json.");

        var response = await gatewayClient!.SearchAsync<Product>(
            "wireless", new SearchOptions { Size = 5, IncludeTotalCount = true });

        Assert.True(response.Value.TotalCount > 0, "Expected at least one hit for 'wireless' via gateway.");
    }

    [SkippableFact]
    public async Task QueryViaGateway_WildcardReturnsAllDocuments()
    {
        Skip.If(!_env.IsConfigured, "No real Azure Search endpoints in appsettings.json.");
        var gatewayClient = _env.TryCreateGatewayClient();
        Skip.If(gatewayClient is null, "Gateway URL not configured in appsettings.json.");

        var response = await gatewayClient!.SearchAsync<Product>(
            "*", new SearchOptions { Size = 0, IncludeTotalCount = true });

        Assert.Equal(SampleData.Products.Count, (int)response.Value.TotalCount!.Value);
    }
}
