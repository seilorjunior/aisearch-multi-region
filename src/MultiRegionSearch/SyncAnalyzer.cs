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

            // Field-level drift comparison: baseline = first region, compare against all others.
            var baseline = perRegion[regionNames[0]][id];
            foreach (var regionName in regionNames.Skip(1))
            {
                var other = perRegion[regionName][id];

                if (baseline.Name != other.Name)
                    issues.Add(new SyncIssue(id, regionName, SyncIssueKind.Drift,
                        $"Name: '{baseline.Name}' vs '{other.Name}'"));

                if (baseline.Description != other.Description)
                    issues.Add(new SyncIssue(id, regionName, SyncIssueKind.Drift, "Description differs"));

                if (baseline.Category != other.Category)
                    issues.Add(new SyncIssue(id, regionName, SyncIssueKind.Drift,
                        $"Category: '{baseline.Category}' vs '{other.Category}'"));

                if (baseline.Price != other.Price)
                    issues.Add(new SyncIssue(id, regionName, SyncIssueKind.Drift,
                        $"Price: {baseline.Price} vs {other.Price}"));

                if (baseline.Rating != other.Rating)
                    issues.Add(new SyncIssue(id, regionName, SyncIssueKind.Drift,
                        $"Rating: {baseline.Rating} vs {other.Rating}"));

                if (!baseline.Tags.SequenceEqual(other.Tags))
                    issues.Add(new SyncIssue(id, regionName, SyncIssueKind.Drift, "Tags differ"));
            }
        }

        return new SyncCheckResult(issues.Count == 0, issues);
    }
}
