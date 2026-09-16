using System.Text.Json;
using Microsoft.Extensions.Configuration;

public sealed record CommandOptions(string Command, string[] Arguments, string? ConfigPath, bool Json)
{
    public static CommandOptions Parse(string[] args)
    {
        var positional = new List<string>();
        string? configPath = null;
        var json = false;
        var help = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json": json = true; break;
                case "--help": case "-h": help = true; break;
                case "--config":
                    if (configPath is not null || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--"))
                        throw new ArgumentException("--config requires one file path.");
                    configPath = args[i];
                    break;
                default:
                    if (args[i].StartsWith("--")) throw new ArgumentException($"Unknown option '{args[i]}'.");
                    positional.Add(args[i]);
                    break;
            }
        }
        var command = help || positional.Count == 0 ? "help" : positional[0].ToLowerInvariant();
        var values = positional.Skip(1).ToArray();
        if (help) values = Array.Empty<string>();
        var valid = command switch
        {
            "help" or "init" or "seed" or "replay" or "status" or "sync-check" or "demo" => values.Length == 0,
            "query" => values.Length <= 1,
            "query-direct" => values.Length is 1 or 2 && !string.IsNullOrWhiteSpace(values[0]),
            "bench" => values.Length <= 2 &&
                (values.Length < 1 || int.TryParse(values[0], out var count) && count is >= 1 and <= 100000) &&
                (values.Length < 2 || int.TryParse(values[1], out var parallelism) && parallelism is >= 1 and <= 256),
            _ => false
        };
        if (!valid) throw new ArgumentException("Invalid command or arguments. Use --help. bench count: 1..100000, concurrency: 1..256.");
        return new CommandOptions(command, values, configPath, json);
    }
}

public sealed class CommandLog(TextWriter output, TextWriter error, bool json)
{
    private readonly object gate = new();
    public void Write(string command, string message, object? data = null, bool failure = false)
    {
        lock (gate)
        {
            var writer = failure ? error : output;
            writer.WriteLine(json
                ? JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow, level = failure ? "error" : "info", command, message, data })
                : $"[{command}] {message}");
        }
    }
}

public static class CliApplication
{
    public const string Help = """
        MultiRegionSearch — Azure AI Search regional replication sample
        Usage: dotnet run -- <command> [args] [--config <path>] [--json]
          init                          Create/update indexes in every region
          seed                          Journal then replicate sample documents
          replay                        Retry only unacknowledged region/document writes
          query [text]                  Query gateway (default *)
          query-direct <region> [text]  Query one region
          status                        Report each region's document count
          sync-check                    Compare all regions; unavailable is not empty
          bench [count] [concurrency]    Defaults 50/4; bounds 1..100000 / 1..256
          demo                          init + seed + bounded readiness + query + status
          help, --help, -h               Help without configuration or credentials
        Global: --config <path> overrides appsettings.json location; --json emits JSON lines.
        Exit: 0 success, 1 operation/drift/unavailable, 2 arguments/configuration, 130 cancelled.
        Auth: DefaultAzureCredential. TLS validation is on unless explicitly disabled in config.
        Replication: use one persistent local journal for all writers; replay before new batches.
        """;

    public static async Task<int> RunAsync(string[] args, Func<string?, SearchConfig>? loadConfig = null,
        Func<SearchConfig, ISearchService>? serviceFactory = null, TextWriter? output = null,
        TextWriter? error = null, CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        var log = new CommandLog(output, error, args.Contains("--json"));
        CommandOptions options;
        SearchConfig settings;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            options = CommandOptions.Parse(args);
            if (options.Command == "help")
            {
                log.Write("help", Help);
                return 0;
            }
            settings = (loadConfig ?? LoadConfiguration)(options.ConfigPath);
            settings.Validate(options.Command is "query" or "bench" or "demo");
            if (options.Command == "query-direct" && !settings.Regions.Any(r =>
                string.Equals(r.Name, options.Arguments[0], StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Unknown region '{options.Arguments[0]}'.");
            if (options.Command == "sync-check" && settings.Regions.Count < 2)
                throw new ArgumentException("sync-check requires at least two configured regions.");
        }
        catch (OperationCanceledException) { log.Write("cancel", "Cancelled.", failure: true); return 130; }
        catch (Exception ex) { log.Write("configuration", ex.Message, failure: true); return 2; }

        try
        {
            if (settings.Gateway.AllowSelfSignedCert)
                log.Write("tls", "WARNING: TLS certificate validation is DISABLED for the gateway. Development only; vulnerable to interception.", failure: true);
            var service = (serviceFactory ?? (s => new AzureSearchService(s)))(settings);
            try { return await new CommandRunner(settings, service, log).RunAsync(options, cancellationToken); }
            finally { (service as IDisposable)?.Dispose(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log.Write(options.Command, "Cancelled. Pending replication is retained; run replay.", failure: true);
            return 130;
        }
        catch (Exception ex) { log.Write(options.Command, ex.Message, failure: true); return 1; }
    }

    public static SearchConfig LoadConfiguration(string? path)
    {
        var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(path is null ? "appsettings.json" : Path.GetFullPath(path), optional: path is null)
            .AddEnvironmentVariables().Build();
        return config.GetSection("Search").Get<SearchConfig>()
            ?? throw new ArgumentException("Missing Search configuration; use --config <path> or Search__... environment variables.");
    }
}
