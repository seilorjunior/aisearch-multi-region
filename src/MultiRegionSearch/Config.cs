public sealed class SearchConfig
{
    public string IndexName { get; set; } = "products";
    public GatewayConfig Gateway { get; set; } = new();
    public List<RegionConfig> Regions { get; set; } = new();
}

public sealed class GatewayConfig
{
    /// <summary>HTTPS URL of the Application Gateway in front of every region.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Demo only: accept the gateway's self-signed certificate. In production use a real
    /// certificate / custom domain and set this to false.
    /// </summary>
    public bool AllowSelfSignedCert { get; set; } = true;

    /// <summary>
    /// Returns <c>true</c> when <see cref="Url"/> is a valid, non-placeholder value.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Url) &&
        !Url.Contains("REPLACE", StringComparison.OrdinalIgnoreCase);
}

public sealed class RegionConfig
{
    public string Name { get; set; } = "";

    /// <summary>Direct https://&lt;name&gt;.search.windows.net endpoint (used for writes + index management).</summary>
    public string Endpoint { get; set; } = "";
}
