using System.Text.Json;

public sealed class ReplicationBatch
{
    public int FormatVersion { get; set; } = 1;
    public long Sequence { get; set; }
    public string IndexName { get; set; } = "";
    public Dictionary<string, string> Endpoints { get; set; } = new();
    public List<Product> Documents { get; set; } = new();
    public Dictionary<string, HashSet<string>> Acknowledged { get; set; } = new();
    public bool Complete => Endpoints.Keys.All(r => Documents.All(d => Acknowledged[r].Contains(d.Id)));
}

/// <summary>
/// A single-local-writer, at-least-once journal. Every writer must use the same
/// persistent path. Sequence orders batches locally, not documents in Azure:
/// external writers, separate journals and delayed service requests have no CAS
/// protection. Atomic rename/Flush(true) recover process crashes on local disks;
/// filesystem/device power-loss guarantees and distributed locking are out of scope.
/// </summary>
public sealed class ReplicationJournal : IDisposable
{
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string path;
    private readonly FileStream writerLock;
    public ReplicationBatch? Batch { get; private set; }

    public ReplicationJournal(SearchConfig settings)
    {
        path = Path.GetFullPath(settings.ReplicationJournalPath);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        else
            Directory.CreateDirectory(Path.GetDirectoryName(path)!, PrivateFileMode | UnixFileMode.UserExecute);
        // Keep the lock file: deleting it would permit processes to lock different inodes.
        writerLock = OpenPrivateFile(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite);
        try
        {
            if (File.Exists(path))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, PrivateFileMode);
                Batch = JsonSerializer.Deserialize<ReplicationBatch>(File.ReadAllText(path))
                    ?? throw new InvalidDataException("Empty replication journal.");
                Validate(settings, Batch);
            }
            // A crash before the atomic rename cannot have sent this staged state.
            if (File.Exists(path + ".new")) File.Delete(path + ".new");
        }
        catch
        {
            writerLock.Dispose();
            throw;
        }
    }

    private static void Validate(SearchConfig settings, ReplicationBatch batch)
    {
        if (batch.FormatVersion != 1 || batch.Sequence < 1 || batch.Documents is null ||
            batch.Documents.Count == 0 || batch.Documents.Any(d => d is null || string.IsNullOrWhiteSpace(d.Id)) ||
            batch.Documents.Select(d => d.Id).Distinct().Count() != batch.Documents.Count ||
            batch.Endpoints is null || batch.Acknowledged is null ||
            batch.IndexName != settings.IndexName || batch.Endpoints.Count != settings.Regions.Count ||
            settings.Regions.Any(r => !batch.Endpoints.TryGetValue(r.Name, out var endpoint) ||
                endpoint != new Uri(r.Endpoint).GetLeftPart(UriPartial.Authority)) ||
            batch.Acknowledged.Count != batch.Endpoints.Count ||
            batch.Endpoints.Keys.Any(r => !batch.Acknowledged.TryGetValue(r, out var ids) || ids is null ||
                ids.Any(id => !batch.Documents.Any(d => d.Id == id))))
            throw new InvalidDataException("Invalid journal or destination configuration changed. Restore the original journal/configuration; do not discard pending writes.");
    }

    public void Begin(SearchConfig settings, IReadOnlyList<Product> documents)
    {
        if (Batch is { Complete: false })
            throw new InvalidOperationException("Pending replication exists. Run replay before starting a new seed/demo batch.");
        Batch = new ReplicationBatch
        {
            Sequence = checked((Batch?.Sequence ?? 0) + 1),
            IndexName = settings.IndexName,
            Endpoints = settings.Regions.ToDictionary(r => r.Name, r => new Uri(r.Endpoint).GetLeftPart(UriPartial.Authority)),
            // Snapshot the payload so retries cannot silently change it.
            Documents = JsonSerializer.Deserialize<List<Product>>(JsonSerializer.Serialize(documents))!,
            Acknowledged = settings.Regions.ToDictionary(r => r.Name, _ => new HashSet<string>())
        };
        Validate(settings, Batch);
        Save();
    }

    public void Save()
    {
        using (var file = OpenPrivateFile(path + ".new", FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(file, Batch);
            file.Flush(flushToDisk: true);
        }
        // Same-directory rename is atomic on supported local filesystems. The old state
        // remains usable after interruption; unacknowledged writes may safely be repeated.
        File.Move(path + ".new", path, overwrite: true);
    }

    private static FileStream OpenPrivateFile(string filePath, FileMode mode, FileAccess access)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFileMode;
        var stream = new FileStream(filePath, options);
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(filePath, PrivateFileMode);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public void Dispose() => writerLock.Dispose();
}
