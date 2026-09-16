using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;

public sealed class SearchServiceTests
{
    [Theory]
    [InlineData(1001)]
    [InlineData(2000)]
    [InlineData(2505)]
    public async Task FetchRetrievesEveryPageThroughActualSdkTransport(int count)
    {
        using var handler = new SearchHandler(count);
        using var http = new HttpClient(handler);
        var settings = Settings();
        using var service = new AzureSearchService(settings, new OfflineCredential(), new HttpClientTransport(http));
        var documents = await service.FetchAsync(settings.Regions[0], CancellationToken.None);
        Assert.Equal(count, documents.Count);
        Assert.Contains((count - 1).ToString(), documents.Keys);
        Assert.Equal(Enumerable.Range(0, (count + 999) / 1000).Select(i => i * 1000), handler.Skips);
        Assert.All(handler.Orders, order => Assert.Equal("Id asc", order));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1000)]
    public async Task LegacyUnsortableKeySupportsOnlySinglePage(int count)
    {
        using var handler = new SearchHandler(count, unsortableKey: true);
        using var http = new HttpClient(handler);
        var settings = Settings();
        using var service = new AzureSearchService(settings, new OfflineCredential(), new HttpClientTransport(http));
        Assert.Equal(count, (await service.FetchAsync(settings.Regions[0], CancellationToken.None)).Count);
        Assert.Equal(new[] { 0, 0 }, handler.Skips);
        Assert.Equal(new string?[] { "Id asc", null }, handler.Orders);
    }

    [Fact]
    public async Task LegacyUnsortableKeyRejectsUnsafeMultiPageComparison()
    {
        using var handler = new SearchHandler(1001, unsortableKey: true);
        using var http = new HttpClient(handler);
        var settings = Settings();
        using var service = new AzureSearchService(settings, new OfflineCredential(), new HttpClientTransport(http));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.FetchAsync(settings.Regions[0], CancellationToken.None));
        Assert.Contains("sortable Id", error.Message);
        Assert.Equal(new[] { 0, 0 }, handler.Skips);
    }

    [Fact]
    public async Task SyncCommandDetectsDriftPastFirstThousandDocuments()
    {
        using var handler = new SearchHandler(1501, driftInWest: true);
        using var http = new HttpClient(handler);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var settings = Settings();
        Assert.Equal(1, await CliApplication.RunAsync(["sync-check"], _ => settings,
            s => new AzureSearchService(s, new OfflineCredential(), new HttpClientTransport(http)), output, error));
        Assert.Contains("1500", error.ToString());
        Assert.Contains("Drift", error.ToString());
    }

    [Fact]
    public async Task FailedLaterPageCannotBeMistakenForPartialSuccessfulSnapshot()
    {
        using var handler = new SearchHandler(1501, failSecondPage: true);
        using var http = new HttpClient(handler);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await CliApplication.RunAsync(["sync-check"], _ => Settings(),
            s => new AzureSearchService(s, new OfflineCredential(), new HttpClientTransport(http)), output, error));
        Assert.Contains("UNAVAILABLE", error.ToString());
        Assert.DoesNotContain("OK", output.ToString());
    }

    private static SearchConfig Settings() => new()
    {
        Regions =
        [
            new RegionConfig { Name = "east", Endpoint = "https://east.example.com" },
            new RegionConfig { Name = "west", Endpoint = "https://west.example.com" }
        ]
    };

    private sealed class OfflineCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("offline-test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class SearchHandler(int total, bool driftInWest = false, bool failSecondPage = false,
        bool unsortableKey = false) : HttpMessageHandler
    {
        public List<int> Skips { get; } = new();
        public List<string?> Orders { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var skip = body.RootElement.TryGetProperty("skip", out var value) ? value.GetInt32() : 0;
            var size = body.RootElement.GetProperty("top").GetInt32();
            Skips.Add(skip);
            var order = body.RootElement.TryGetProperty("orderby", out var ordering) ? ordering.GetString() : null;
            Orders.Add(order);
            if (unsortableKey && order is not null)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"code":"InvalidRequest","message":"Id is not sortable"}}""", Encoding.UTF8, "application/json")
                };
            if (failSecondPage && skip > 0)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":{"code":"InvalidRequest","message":"test page failure"}}""", Encoding.UTF8, "application/json")
                };
            var documents = Enumerable.Range(skip, Math.Min(size, total - skip)).Select(i =>
                new Product { Id = i.ToString(), Name = driftInWest && request.RequestUri!.Host.StartsWith("west") && i == 1500 ? "changed" : "original" });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["@odata.count"] = total, ["value"] = documents
                }), Encoding.UTF8, "application/json")
            };
        }
    }
}
