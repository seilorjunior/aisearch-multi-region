public enum SyncIssueKind { Missing, Drift }

public sealed record SyncIssue(
    string DocumentId,
    string Region,
    SyncIssueKind Kind,
    string Detail);

public sealed record SyncCheckResult(bool InSync, IReadOnlyList<SyncIssue> Issues);

/// <summary>
/// Pure comparison logic for multi-region document sync checking.
/// Accepts pre-fetched per-region documents and returns all detected issues.
/// </summary>
public static class SyncAnalyzer
{
    public static SyncCheckResult Analyze(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, Product>> perRegion)
    {
        if (perRegion.Count < 2)
            return new SyncCheckResult(true, Array.Empty<SyncIssue>());

        var regionNames = perRegion.Keys.ToList();
        var allIds = perRegion.Values
            .SelectMany(d => d.Keys)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var issues = new List<SyncIssue>();

        foreach (var id in allIds)
        {
            var missingIn = regionNames.Where(r => !perRegion[r].ContainsKey(id)).ToList();

            if (missingIn.Count > 0)
            {
                foreach (var region in missingIn)
                    issues.Add(new SyncIssue(id, region, SyncIssueKind.Missing, $"absent in {region}"));
                continue;
            }

            // Field-level drift: baseline = first region, compare against all others.
            var baseline = perRegion[regionNames[0]][id];
            foreach (var regionName in regionNames.Skip(1))
            {
                var other = perRegion[regionName][id];
                if (!baseline.Equals(other))
                    issues.Add(new SyncIssue(id, regionName, SyncIssueKind.Drift,
                        "one or more fields differ (re-run with verbose flag for details)"));
            }
        }

        return new SyncCheckResult(issues.Count == 0, issues);
    }
}
