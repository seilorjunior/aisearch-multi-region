public class SearchConfigTests
{
    [Fact]
    public void DefaultIndexName_IsProducts()
    {
        var config = new SearchConfig();
        Assert.Equal("products", config.IndexName);
    }

    [Fact]
    public void DefaultGateway_IsNotNull()
    {
        var config = new SearchConfig();
        Assert.NotNull(config.Gateway);
    }

    [Fact]
    public void DefaultRegions_IsEmptyList()
    {
        var config = new SearchConfig();
        Assert.NotNull(config.Regions);
        Assert.Empty(config.Regions);
    }

    [Fact]
    public void IndexName_CanBeOverridden()
    {
        var config = new SearchConfig { IndexName = "custom-index" };
        Assert.Equal("custom-index", config.IndexName);
    }
}

public class GatewayConfigTests
{
    [Fact]
    public void DefaultUrl_IsEmpty()
    {
        var config = new GatewayConfig();
        Assert.Equal("", config.Url);
    }

    [Fact]
    public void DefaultAllowSelfSignedCert_IsTrue()
    {
        var config = new GatewayConfig();
        Assert.True(config.AllowSelfSignedCert);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("https://REPLACE_WITH_GATEWAY_URL", false)]
    [InlineData("https://replace.example.com", false)]        // "REPLACE" case-insensitive
    [InlineData("https://my-appgw.eastus.cloudapp.azure.com", true)]
    [InlineData("http://10.0.0.1", true)]
    public void IsConfigured_ReturnsExpected(string url, bool expected)
    {
        var config = new GatewayConfig { Url = url };
        Assert.Equal(expected, config.IsConfigured);
    }
}

public class RegionConfigTests
{
    [Fact]
    public void DefaultName_IsEmpty()
    {
        var config = new RegionConfig();
        Assert.Equal("", config.Name);
    }

    [Fact]
    public void DefaultEndpoint_IsEmpty()
    {
        var config = new RegionConfig();
        Assert.Equal("", config.Endpoint);
    }

    [Fact]
    public void Properties_AreSettable()
    {
        var config = new RegionConfig
        {
            Name     = "eastus",
            Endpoint = "https://my-search.search.windows.net"
        };
        Assert.Equal("eastus", config.Name);
        Assert.Equal("https://my-search.search.windows.net", config.Endpoint);
    }
}
