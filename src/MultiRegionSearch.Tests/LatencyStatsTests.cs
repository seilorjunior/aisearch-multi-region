public class LatencyStatsTests
{
    [Fact]
    public void Compute_EmptyList_ReturnsNull()
    {
        Assert.Null(LatencyCalculator.Compute(Array.Empty<long>()));
    }

    [Fact]
    public void Compute_SingleElement_AllPercentilesAreTheSameValue()
    {
        var result = LatencyCalculator.Compute(new long[] { 100 });
        Assert.NotNull(result);
        Assert.Equal(100, result!.Min);
        Assert.Equal(100, result.P50);
        Assert.Equal(100, result.P95);
        Assert.Equal(100, result.Max);
    }

    [Fact]
    public void Compute_UnsortedInput_SortsCorrectly()
    {
        // Deliberate out-of-order input
        var result = LatencyCalculator.Compute(new long[] { 300, 100, 200 });
        Assert.NotNull(result);
        Assert.Equal(100, result!.Min);
        Assert.Equal(300, result.Max);
    }

    [Fact]
    public void Compute_P50_IsMedian()
    {
        // Sorted: [10, 20, 30, 40, 50] — Count=5, index=2 → 30
        var result = LatencyCalculator.Compute(new long[] { 50, 10, 30, 20, 40 });
        Assert.NotNull(result);
        Assert.Equal(30, result!.P50);
    }

    [Fact]
    public void Compute_HundredElements_PercentilesMatchFormula()
    {
        // Sorted: [1, 2, ..., 100]
        // P50: sorted[100/2]       = sorted[50] = 51
        // P95: sorted[(int)(100*0.95)] = sorted[95] = 96
        var latencies = Enumerable.Range(1, 100).Select(i => (long)i).ToArray();
        var result = LatencyCalculator.Compute(latencies);
        Assert.NotNull(result);
        Assert.Equal(1,   result!.Min);
        Assert.Equal(100, result.Max);
        Assert.Equal(51,  result.P50);
        Assert.Equal(96,  result.P95);
    }

    [Fact]
    public void Compute_TwentyElements_P95IsNinetyFifthPercentileIndex()
    {
        // Sorted: [10, 20, ..., 200]
        // P95: sorted[(int)(20*0.95)] = sorted[19] = 200
        var latencies = Enumerable.Range(1, 20).Select(i => (long)i * 10).ToArray();
        var result = LatencyCalculator.Compute(latencies);
        Assert.NotNull(result);
        Assert.Equal(10,  result!.Min);
        Assert.Equal(200, result.Max);
        Assert.Equal(200, result.P95);
    }

    [Fact]
    public void Compute_AllSameValues_AllPercentilesEqual()
    {
        var latencies = Enumerable.Repeat(42L, 100).ToArray();
        var result = LatencyCalculator.Compute(latencies);
        Assert.NotNull(result);
        Assert.Equal(42, result!.Min);
        Assert.Equal(42, result.P50);
        Assert.Equal(42, result.P95);
        Assert.Equal(42, result.Max);
    }

    [Fact]
    public void Compute_MaxIsAlwaysGreaterThanOrEqualToMin()
    {
        var latencies = new long[] { 5, 3, 9, 1, 7 };
        var result = LatencyCalculator.Compute(latencies);
        Assert.NotNull(result);
        Assert.True(result!.Max >= result.Min);
    }

    [Fact]
    public void Compute_P95IsAlwaysGreaterThanOrEqualToP50()
    {
        var latencies = new long[] { 5, 3, 9, 1, 7, 2, 8, 6, 4, 10 };
        var result = LatencyCalculator.Compute(latencies);
        Assert.NotNull(result);
        Assert.True(result!.P95 >= result.P50);
    }
}
