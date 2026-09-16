public sealed class SearchConfig
{
    public string IndexName { get; set; } = "products";
    public GatewayConfig Gateway { get; set; } = new();
    public List<RegionConfig> Regions { get; set; } = new();
    public string ReplicationJournalPath { get; set; } = "replication-journal.json";
    public int ReadinessTimeoutSeconds { get; set; } = 60;
    public int ReadinessPollIntervalMilliseconds { get; set; } = 500;

    public void Validate(bool requireGateway = false)
    {
        if (string.IsNullOrWhiteSpace(IndexName) ||
            !System.Text.RegularExpressions.Regex.IsMatch(IndexName, "^[a-z0-9][a-z0-9-]{1,127}$") ||
            IndexName.Contains("--") || IndexName.EndsWith('-'))
            throw new ArgumentException("Search.IndexName must be a valid nonempty Azure Search index name.");
        if (Regions is null || Regions.Count == 0)
            throw new ArgumentException("Search.Regions must contain at least one region.");
        if (Regions.Any(r => r is null || string.IsNullOrWhiteSpace(r.Name) || r.Name != r.Name.Trim() ||
                             !GatewayConfig.IsValidEndpoint(r.Endpoint)) ||
            Regions.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Regions.Count ||
            Regions.Select(r => new Uri(r.Endpoint).GetLeftPart(UriPartial.Authority))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != Regions.Count)
            throw new ArgumentException("Regions require unique nonempty names and unique HTTPS root endpoints.");
        if (Gateway is null || (requireGateway || !string.IsNullOrWhiteSpace(Gateway.Url)) && !Gateway.IsConfigured)
            throw new ArgumentException("Search.Gateway.Url must be an HTTPS root URL without credentials, query or fragment.");
        if (string.IsNullOrWhiteSpace(ReplicationJournalPath))
            throw new ArgumentException("Search.ReplicationJournalPath must not be empty.");
        if (ReadinessTimeoutSeconds is < 1 or > 3600 || ReadinessPollIntervalMilliseconds is < 1 or > 60000)
            throw new ArgumentException("Readiness timeout must be 1..3600 seconds and polling interval 1..60000 milliseconds.");
    }
}

public sealed class GatewayConfig
{
    /// <summary>HTTPS URL of the Application Gateway in front of every region.</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Demo only: accept the gateway's self-signed certificate. In production use a real
    /// certificate / custom domain and set this to false.
    /// </summary>
    public bool AllowSelfSignedCert { get; set; } = false;

    /// <summary>
    /// Returns <c>true</c> when <see cref="Url"/> is a valid, non-placeholder value.
    /// </summary>
    public bool IsConfigured => IsValidEndpoint(Url);

    public static bool IsValidEndpoint(string? url) =>
        !string.IsNullOrWhiteSpace(url) && url == url.Trim() &&
        !url.Contains("REPLACE", StringComparison.OrdinalIgnoreCase) &&
        !url.Contains('\\') &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) && uri.AbsolutePath == "/" &&
        // Reject normalized paths (such as /a/..) and even empty query/fragment delimiters.
        !url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/').Contains('/') &&
        !url.Contains('?') && !url.Contains('#');
}

public sealed class RegionConfig
{
    public string Name { get; set; } = "";

    /// <summary>Direct https://&lt;name&gt;.search.windows.net endpoint (used for writes + index management).</summary>
    public string Endpoint { get; set; } = "";
}
