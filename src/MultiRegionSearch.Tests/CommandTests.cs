using System.Text.Json;

public sealed class CommandTests : IDisposable
{
    private readonly string directory = Path.Combine(Directory.GetCurrentDirectory(), "command-test-state-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSearchService service = new();
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private SearchConfig Settings => new()
    {
        Gateway = new GatewayConfig { Url = "https://gateway.example.com" },
        Regions =
        [
            new RegionConfig { Name = "east", Endpoint = "https://east.example.com" },
            new RegionConfig { Name = "west", Endpoint = "https://west.example.com" }
        ],
        ReplicationJournalPath = Path.Combine(directory, "journal.json"),
        ReadinessTimeoutSeconds = 1,
        ReadinessPollIntervalMilliseconds = 10
    };

    private Task<int> Run(string[] args, SearchConfig? settings = null, CancellationToken cancellationToken = default) =>
        CliApplication.RunAsync(args, _ => settings ?? Settings, _ => service, output, error, cancellationToken);

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task HelpDoesNotLoadConfigurationOrCreateService(string help)
    {
        Assert.Equal(0, await CliApplication.RunAsync([help],
            _ => throw new Exception("Must not load config"), _ => throw new Exception("Must not create service"), output, error));
        Assert.Contains("Usage:", output.ToString());
    }

    [Fact]
    public async Task NoArgumentsShowsHelpWithoutConfiguration() =>
        Assert.Equal(0, await CliApplication.RunAsync([], _ => throw new Exception(), output: output, error: error));

    [Theory]
    [InlineData("typo")]
    [InlineData("bench", "0")]
    [InlineData("bench", "-1")]
    [InlineData("bench", "100001")]
    [InlineData("bench", "foo")]
    [InlineData("bench", "1", "0")]
    [InlineData("bench", "1", "257")]
    [InlineData("bench", "1", "foo")]
    [InlineData("seed", "extra")]
    [InlineData("query-direct")]
    [InlineData("--config")]
    [InlineData("--unknown")]
    public async Task InvalidArgumentsFailBeforeConfiguration(params string[] args)
    {
        Assert.Equal(2, await CliApplication.RunAsync(args,
            _ => throw new Exception("Must not load config"), output: output, error: error));
        Assert.DoesNotContain("Must not", error.ToString());
    }

    [Theory]
    [InlineData("status")]
    [InlineData("init")]
    [InlineData("seed")]
    [InlineData("sync-check")]
    public async Task NoRegionsFailsConfiguration(string command)
    {
        var settings = Settings;
        settings.Regions.Clear();
        Assert.Equal(2, await Run([command], settings));
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task InvalidConfigurationAndUnknownRegionFail()
    {
        var settings = Settings;
        settings.IndexName = "";
        Assert.Equal(2, await Run(["init"], settings));
        settings = Settings;
        settings.Regions[1].Name = "EAST";
        Assert.Equal(2, await Run(["init"], settings));
        Assert.Equal(2, await Run(["query-direct", "unknown"]));
    }

    [Fact]
    public async Task ConfigPathIsPassedToLoader()
    {
        string? actual = null;
        Assert.Equal(0, await CliApplication.RunAsync(["status", "--config", "chosen.json"],
            path => { actual = path; return Settings; }, _ => service, output, error));
        Assert.Equal("chosen.json", actual);
    }

    [Fact]
    public async Task SyncUnavailableIsNotEmptyOrMissing()
    {
        service.Fetch = (r, _) => r.Name == "west"
            ? throw new IOException("offline") : Task.FromResult<IReadOnlyDictionary<string, Product>>(SampleData.Products.ToDictionary(p => p.Id));
        Assert.Equal(1, await Run(["sync-check"]));
        Assert.Contains("UNAVAILABLE", error.ToString());
        Assert.DoesNotContain("Missing", error.ToString());
        Assert.DoesNotContain("OK", output.ToString());
    }

    [Fact]
    public async Task SyncAllUnavailableCannotReportSuccess()
    {
        service.Fetch = (_, _) => throw new IOException("offline");
        Assert.Equal(1, await Run(["sync-check"]));
        Assert.Contains("INCONCLUSIVE", error.ToString());
        Assert.DoesNotContain("OK", output.ToString());
    }

    [Fact]
    public async Task SyncTwoSuccessfullyEmptyRegionsAreInSync() =>
        Assert.Equal(0, await Run(["sync-check"]));

    [Fact]
    public async Task SyncDriftReturnsFailure()
    {
        service.Fetch = (r, _) => Task.FromResult<IReadOnlyDictionary<string, Product>>(
            r.Name == "east" ? new Dictionary<string, Product> { ["1"] = new() { Id = "1" } } : new Dictionary<string, Product>());
        Assert.Equal(1, await Run(["sync-check"]));
        Assert.Contains("Missing", error.ToString());
    }

    [Fact]
    public async Task StatusFailureIsNonzeroAndOtherRegionsStillReported()
    {
        service.Query = (r, _, _, _) => r?.Name == "east" ? throw new IOException("offline") :
            Task.FromResult(new QueryResult(0, Array.Empty<Product>()));
        Assert.Equal(1, await Run(["status"]));
        Assert.Contains("west: 0", output.ToString());
    }

    [Fact]
    public async Task InitAndGatewayFailuresAreNonzero()
    {
        service.Initialize = (_, _) => throw new IOException("offline");
        Assert.Equal(1, await Run(["init"]));
        service.Query = (_, _, _, _) => throw new IOException("offline");
        Assert.Equal(1, await Run(["query"]));
        Assert.Equal(1, await Run(["query-direct", "east"]));
    }

    [Fact]
    public async Task BenchmarkReportsFailureLatencyAndThroughputAsJson()
    {
        service.Query = (_, _, _, _) => throw new IOException("offline");
        Assert.Equal(1, await Run(["bench", "3", "2", "--json"]));
        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines) using (JsonDocument.Parse(line)) { }
        using var summary = JsonDocument.Parse(lines[0]);
        var data = summary.RootElement.GetProperty("data");
        Assert.Equal(3, data.GetProperty("failed").GetInt32());
        Assert.True(data.GetProperty("requestsPerSecond").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Object, data.GetProperty("failedLatency").ValueKind);
    }

    [Fact]
    public async Task ExplicitInsecureTlsEmitsWarning()
    {
        var settings = Settings;
        settings.Gateway.AllowSelfSignedCert = true;
        Assert.Equal(0, await Run(["query"], settings));
        Assert.Contains("TLS certificate validation is DISABLED", error.ToString());
    }

    [Fact]
    public async Task PartialIndexingFailureIsJournaledBeforeSendAndReplaysOnlyFailedDocuments()
    {
        var failId = SampleData.Products[0].Id;
        service.Write = (region, documents, _) =>
        {
            using var state = JsonDocument.Parse(File.ReadAllText(Settings.ReplicationJournalPath));
            Assert.True(state.RootElement.GetProperty("Sequence").GetInt64() > 0);
            return Task.FromResult<IReadOnlyList<DocumentWriteResult>>(documents.Select(d =>
                new DocumentWriteResult(d.Id, region.Name != "west" || d.Id != failId, 200)).ToArray());
        };
        Assert.Equal(1, await Run(["seed"]));
        Assert.Equal(2, service.Writes.Count);
        Assert.Equal(1, await Run(["seed"]));
        Assert.Equal(2, service.Writes.Count);
        service.Write = FakeSearchService.SuccessfulWrite;
        Assert.Equal(0, await Run(["replay"]));
        var replay = service.Writes.Last();
        Assert.Equal("west", replay.Region);
        Assert.Equal(failId, Assert.Single(replay.Ids));
        Assert.Equal(0, await Run(["replay"]));
        Assert.Equal(3, service.Writes.Count);
        Assert.Equal(0, await Run(["seed"]));
        using var journal = new ReplicationJournal(Settings);
        Assert.Equal(2, journal.Batch!.Sequence);
    }

    [Fact]
    public async Task MissingWriteAcknowledgmentRemainsPending()
    {
        service.Write = (_, _, _) => Task.FromResult<IReadOnlyList<DocumentWriteResult>>(Array.Empty<DocumentWriteResult>());
        Assert.Equal(1, await Run(["seed"]));
        using var journal = new ReplicationJournal(Settings);
        Assert.All(journal.Batch!.Acknowledged.Values, Assert.Empty);
    }

    [Fact]
    public async Task UnavailableWriteRegionIsRetriedWithoutResendingSuccessfulRegion()
    {
        service.Write = (r, docs, ct) => r.Name == "east"
            ? throw new IOException("offline") : FakeSearchService.SuccessfulWrite(r, docs, ct);
        Assert.Equal(1, await Run(["seed"]));
        Assert.Equal(2, service.Writes.Count);
        service.Write = FakeSearchService.SuccessfulWrite;
        Assert.Equal(0, await Run(["replay"]));
        Assert.Equal("east", service.Writes.Last().Region);
        Assert.Equal(3, service.Writes.Count);
    }

    [Fact]
    public async Task AllWriteRegionsUnavailableRemainPending()
    {
        service.Write = (_, _, _) => throw new IOException("offline");
        Assert.Equal(1, await Run(["seed"]));
        using var journal = new ReplicationJournal(Settings);
        Assert.All(journal.Batch!.Acknowledged.Values, Assert.Empty);
        Assert.Equal(2, service.Writes.Count);
    }

    [Fact]
    public async Task CrashedUnacknowledgedBatchAndStagingFileRecover()
    {
        using (var journal = new ReplicationJournal(Settings)) journal.Begin(Settings, SampleData.Products);
        await File.WriteAllTextAsync(Settings.ReplicationJournalPath + ".new", "interrupted staging write");
        Assert.Equal(0, await Run(["replay"]));
        Assert.Equal(2, service.Writes.Count);
        Assert.All(service.Writes, w => Assert.Equal(SampleData.Products.Count, w.Ids.Length));
        Assert.False(File.Exists(Settings.ReplicationJournalPath + ".new"));
    }

    [Fact]
    public async Task ExclusiveLockPreventsAnotherWriter()
    {
        using var journal = new ReplicationJournal(Settings);
        Assert.Equal(1, await Run(["seed"]));
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task ChangedDestinationAndCorruptJournalFailClosed()
    {
        using (var journal = new ReplicationJournal(Settings)) journal.Begin(Settings, SampleData.Products);
        var changed = Settings;
        changed.Regions[0].Endpoint = "https://other.example.com";
        Assert.Equal(1, await Run(["replay"], changed));
        await File.WriteAllTextAsync(Settings.ReplicationJournalPath, "{broken");
        Assert.Equal(1, await Run(["seed"]));
        Assert.Empty(service.Writes);
    }

    [Fact]
    public async Task CancellationDuringWriteRetainsPendingForReplay()
    {
        using var cancellation = new CancellationTokenSource();
        service.Write = (_, _, ct) =>
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        };
        Assert.Equal(130, await Run(["seed"], cancellationToken: cancellation.Token));
        using (var journal = new ReplicationJournal(Settings))
            Assert.False(journal.Batch!.Complete);
        service.Write = FakeSearchService.SuccessfulWrite;
        Assert.Equal(0, await Run(["replay"]));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("sync-check")]
    [InlineData("bench")]
    [InlineData("query")]
    public async Task CancellationPropagatesFromService(string command)
    {
        using var cancellation = new CancellationTokenSource();
        service.Query = (_, _, _, ct) => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); throw new Exception(); };
        service.Fetch = (_, ct) => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); throw new Exception(); };
        Assert.Equal(130, await Run([command], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ReadinessTimeoutFailsDemoWithoutGatewayQuery()
    {
        service.Query = (_, _, _, _) => throw new Exception("Must not query gateway before ready");
        Assert.Equal(1, await Run(["demo"]));
        Assert.Contains("Timed out", error.ToString());
        Assert.DoesNotContain("Must not query", error.ToString());
    }

    [Fact]
    public async Task DemoWaitsForMatchingDocumentsThenQueries()
    {
        service.Fetch = (_, _) => Task.FromResult<IReadOnlyDictionary<string, Product>>(SampleData.Products.ToDictionary(d => d.Id));
        Assert.Equal(0, await Run(["demo"]));
        Assert.Contains("Sample documents visible", output.ToString());
    }

    [Fact]
    public async Task CancellationDuringReadinessIsNotReportedAsTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        service.Fetch = (_, ct) => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); throw new Exception(); };
        Assert.Equal(130, await Run(["demo"], cancellationToken: cancellation.Token));
        Assert.DoesNotContain("Timed out", error.ToString());
    }

    [Fact]
    public async Task SuccessfulBenchmarkReturnsZero()
    {
        Assert.Equal(0, await Run(["bench", "2", "1"]));
        Assert.Contains("2 ok, 0 failed", output.ToString());
    }

    public void Dispose()
    {
        output.Dispose();
        error.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private sealed class FakeSearchService : ISearchService
    {
        public List<(string Region, string[] Ids)> Writes { get; } = new();
        public Func<RegionConfig, CancellationToken, Task> Initialize { get; set; } = (_, _) => Task.CompletedTask;
        public Func<RegionConfig, IReadOnlyList<Product>, CancellationToken, Task<IReadOnlyList<DocumentWriteResult>>> Write { get; set; } = SuccessfulWrite;
        public Func<RegionConfig?, string, int, CancellationToken, Task<QueryResult>> Query { get; set; } =
            (_, _, _, _) => Task.FromResult(new QueryResult(0, Array.Empty<Product>()));
        public Func<RegionConfig, CancellationToken, Task<IReadOnlyDictionary<string, Product>>> Fetch { get; set; } =
            (_, _) => Task.FromResult<IReadOnlyDictionary<string, Product>>(new Dictionary<string, Product>());
        public static Task<IReadOnlyList<DocumentWriteResult>> SuccessfulWrite(RegionConfig region, IReadOnlyList<Product> docs, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DocumentWriteResult>>(docs.Select(d => new DocumentWriteResult(d.Id, true, 200)).ToArray());
        public Task InitializeAsync(RegionConfig region, CancellationToken cancellationToken) => Initialize(region, cancellationToken);
        public Task<IReadOnlyList<DocumentWriteResult>> WriteAsync(RegionConfig region, IReadOnlyList<Product> documents, CancellationToken cancellationToken)
        {
            Writes.Add((region.Name, documents.Select(d => d.Id).ToArray()));
            return Write(region, documents, cancellationToken);
        }
        public Task<QueryResult> QueryAsync(RegionConfig? region, string text, int size, CancellationToken cancellationToken) =>
            Query(region, text, size, cancellationToken);
        public Task<IReadOnlyDictionary<string, Product>> FetchAsync(RegionConfig region, CancellationToken cancellationToken) => Fetch(region, cancellationToken);
    }
}
