using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;

public sealed record DocumentWriteResult(string Key, bool Succeeded, int Status, string? Error = null);
public sealed record QueryResult(long? TotalCount, IReadOnlyList<Product> Documents);

public interface ISearchService
{
    Task InitializeAsync(RegionConfig region, CancellationToken cancellationToken);
    Task<IReadOnlyList<DocumentWriteResult>> WriteAsync(RegionConfig region, IReadOnlyList<Product> documents, CancellationToken cancellationToken);
    Task<QueryResult> QueryAsync(RegionConfig? region, string text, int size, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, Product>> FetchAsync(RegionConfig region, CancellationToken cancellationToken);
}

public sealed class AzureSearchService : ISearchService, IDisposable
{
    private readonly SearchConfig settings;
    private readonly TokenCredential credential;
    private readonly HttpPipelineTransport? transport;
    private readonly Dictionary<string, SearchClient> clients;
    private readonly SearchClient? gateway;
    private readonly HttpClient? insecureHttpClient;

    public AzureSearchService(SearchConfig settings, TokenCredential? credential = null, HttpPipelineTransport? transport = null)
    {
        this.settings = settings;
        this.credential = credential ?? new DefaultAzureCredential();
        this.transport = transport;
        var directOptions = new SearchClientOptions();
        if (transport is not null) directOptions.Transport = transport;
        clients = settings.Regions.ToDictionary(r => r.Name,
            r => new SearchClient(new Uri(r.Endpoint), settings.IndexName, this.credential, directOptions));
        if (settings.Gateway.IsConfigured)
        {
            var options = new SearchClientOptions();
            if (transport is not null) options.Transport = transport;
            if (settings.Gateway.AllowSelfSignedCert)
            {
                insecureHttpClient = new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                });
                options.Transport = new HttpClientTransport(insecureHttpClient);
            }
            gateway = new SearchClient(new Uri(settings.Gateway.Url), settings.IndexName, this.credential, options);
        }
    }

    public async Task InitializeAsync(RegionConfig region, CancellationToken cancellationToken)
    {
        var index = new SearchIndex(settings.IndexName, new FieldBuilder().Build(typeof(Product)));
        var options = new SearchClientOptions();
        if (transport is not null) options.Transport = transport;
        await new SearchIndexClient(new Uri(region.Endpoint), credential, options)
            .CreateOrUpdateIndexAsync(index, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<DocumentWriteResult>> WriteAsync(
        RegionConfig region, IReadOnlyList<Product> documents, CancellationToken cancellationToken)
    {
        // Keep SDK retries; replay is operator-triggered, not another nested retry loop.
        var response = await clients[region.Name].IndexDocumentsAsync(
            IndexDocumentsBatch.MergeOrUpload(documents),
            new IndexDocumentsOptions { ThrowOnAnyError = false }, cancellationToken);
        return response.Value.Results.Select(r => new DocumentWriteResult(r.Key, r.Succeeded, r.Status, r.ErrorMessage)).ToList();
    }

    public async Task<QueryResult> QueryAsync(RegionConfig? region, string text, int size, CancellationToken cancellationToken)
    {
        var client = region is null ? gateway ?? throw new InvalidOperationException("Gateway is not configured.") : clients[region.Name];
        var response = await client.SearchAsync<Product>(text,
            new SearchOptions { Size = size, IncludeTotalCount = true }, cancellationToken);
        var documents = new List<Product>();
        await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            documents.Add(result.Document);
        return new QueryResult(response.Value.TotalCount, documents);
    }

    public async Task<IReadOnlyDictionary<string, Product>> FetchAsync(RegionConfig region, CancellationToken cancellationToken)
    {
        var documents = new Dictionary<string, Product>();
        long? expectedCount = null;
        var ordered = true;
        // Explicit paging is needed: Size=1000 alone only retrieves the first search page.
        // Search's skip limit makes this sample comparison deliberately bounded.
        for (var skip = 0; skip <= 100000; skip += 1000)
        {
            var options = new SearchOptions { Size = 1000, Skip = skip, IncludeTotalCount = true };
            options.OrderBy.Add($"{nameof(Product.Id)} asc");
            Azure.Response<SearchResults<Product>> response;
            try
            {
                response = await clients[region.Name].SearchAsync<Product>("*", options, cancellationToken);
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 400 && skip == 0)
            {
                // Existing demo indexes have an unsortable key. Only a single page is safe
                // without unique ordering; never use offset paging on those indexes.
                ordered = false;
                options.OrderBy.Clear();
                response = await clients[region.Name].SearchAsync<Product>("*", options, cancellationToken);
            }
            if (response.Value.TotalCount is not long total || expectedCount is long previous && previous != total)
                throw new InvalidOperationException("Document count unavailable or changed during paging; retry sync-check.");
            expectedCount = total;
            if (!ordered && total > 1000)
                throw new InvalidOperationException(
                    "Stable sync-check pagination requires a sortable Id field. Reindex into a new index with Id sortable; the legacy index is unchanged.");
            if (expectedCount > 100000)
                throw new InvalidOperationException("sync-check exceeds the sample's 100,000-document paging bound.");
            var count = 0;
            await foreach (var result in response.Value.GetResultsAsync().WithCancellation(cancellationToken))
            {
                if (!documents.TryAdd(result.Document.Id, result.Document))
                    throw new InvalidOperationException("Documents changed during paging; retry sync-check.");
                count++;
            }
            if (count < 1000 || documents.Count == expectedCount)
            {
                if (documents.Count != expectedCount)
                    throw new InvalidOperationException("Incomplete document snapshot; retry sync-check.");
                return documents;
            }
        }
        throw new InvalidOperationException("sync-check exceeds the sample's 100,000-document paging bound.");
    }

    public void Dispose() => insecureHttpClient?.Dispose();
}
