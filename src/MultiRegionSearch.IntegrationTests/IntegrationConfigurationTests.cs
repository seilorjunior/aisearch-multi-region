using Microsoft.Extensions.Configuration;

public class IntegrationConfigurationTests
{
    private static IConfiguration Configuration(bool required, bool endpoints = false, bool gateway = false) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AZURE_INTEGRATION_REQUIRED"] = required.ToString(),
            ["Search:IndexName"] = "products",
            ["Search:Regions:0:Name"] = "eastus",
            ["Search:Regions:0:Endpoint"] = endpoints ? "https://dedicated-east.search.windows.net" : "",
            ["Search:Regions:1:Name"] = "westus2",
            ["Search:Regions:1:Endpoint"] = endpoints ? "https://dedicated-west.search.windows.net" : "",
            ["Search:Gateway:Url"] = gateway ? "https://gateway.contoso.net" : ""
        }).Build();

    [Fact]
    public void OfflineConfiguration_IsUnconfiguredAndUsesUniqueLowercaseIndexes()
    {
        var first = new SearchEnvironmentFixture(Configuration(false));
        var second = new SearchEnvironmentFixture(Configuration(false));

        Assert.False(first.IsConfigured);
        Assert.Matches("^products-it-[0-9a-f]{32}$", first.Settings.IndexName);
        Assert.NotEqual(first.Settings.IndexName, second.Settings.IndexName);
        Assert.NotEqual(first.Settings.ReplicationJournalPath, second.Settings.ReplicationJournalPath);
    }

    [Fact]
    public void RequiredIntegration_RejectsMissingEndpoints()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new SearchEnvironmentFixture(Configuration(true, gateway: true)));
        Assert.Contains("Integration is required", error.Message);
    }

    [Fact]
    public void RequiredIntegration_RejectsMissingGateway()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SearchEnvironmentFixture(Configuration(true, endpoints: true)));
    }

    [Fact]
    public void RequiredIntegration_AcceptsCompleteConfigurationWithoutContactingAzure()
    {
        var fixture = new SearchEnvironmentFixture(Configuration(true, endpoints: true, gateway: true));
        Assert.True(fixture.IsConfigured);
        Assert.True(fixture.Settings.Gateway.IsConfigured);
    }

    [Fact]
    public async Task Cleanup_BeforeInitializationIsSafeAndRepeatable()
    {
        var fixture = new SearchEnvironmentFixture(Configuration(true, endpoints: true, gateway: true));
        await fixture.DisposeAsync();
        await fixture.DisposeAsync();
    }
}
