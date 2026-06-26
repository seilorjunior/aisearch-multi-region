/// <summary>Immutable latency percentile summary.</summary>
public sealed record LatencyStats(long Min, long P50, long P95, long Max);

/// <summary>
/// Pure latency statistics calculator extracted from BenchAsync for unit testing.
/// </summary>
public static class LatencyCalculator
{
    /// <summary>
    /// Computes min / p50 / p95 / max from an unordered list of latency samples.
    /// Returns <c>null</c> if <paramref name="latencies"/> is empty.
    /// </summary>
    public static LatencyStats? Compute(IReadOnlyList<long> latencies)
    {
        if (latencies.Count == 0)
            return null;

        var sorted = latencies.OrderBy(x => x).ToList();
        return new LatencyStats(
            Min: sorted[0],
            P50: sorted[sorted.Count / 2],
            P95: sorted[(int)(sorted.Count * 0.95)],
            Max: sorted[sorted.Count - 1]);
    }
}
