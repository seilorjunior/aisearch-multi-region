public class SyncAnalyzerTests
{
    // ---- helpers -----------------------------------------------------------

    private static Product MakeProduct(string id, string name = "P", string desc = "D",
        string category = "C", double price = 1.0, double rating = 4.0,
        string[]? tags = null)
        => new() { Id = id, Name = name, Description = desc, Category = category,
                   Price = price, Rating = rating, Tags = tags ?? Array.Empty<string>() };

    private static IReadOnlyDictionary<string, Product> Region(params Product[] products)
        => products.ToDictionary(p => p.Id);

    // ---- single / empty region short-circuit --------------------------------

    [Fact]
    public void Analyze_WithOneRegion_ReturnsInSyncNoIssues()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1"))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.True(result.InSync);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Analyze_WithZeroRegions_ReturnsInSyncNoIssues()
    {
        var result = SyncAnalyzer.Analyze(
            new Dictionary<string, IReadOnlyDictionary<string, Product>>());
        Assert.True(result.InSync);
        Assert.Empty(result.Issues);
    }

    // ---- two identical regions ----------------------------------------------

    [Fact]
    public void Analyze_IdenticalRegions_ReturnsInSync()
    {
        var p = MakeProduct("1", name: "Alpha", tags: new[] { "a", "b" });
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(p),
            ["west"] = Region(MakeProduct("1", name: "Alpha", tags: new[] { "a", "b" }))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.True(result.InSync);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Analyze_BothRegionsEmpty_ReturnsInSync()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(),
            ["west"] = Region()
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.True(result.InSync);
        Assert.Empty(result.Issues);
    }

    // ---- missing documents --------------------------------------------------

    [Fact]
    public void Analyze_DocumentMissingFromOneRegion_ReportsMissingIssue()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1")),
            ["west"] = Region()   // doc "1" absent
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("1", issue.DocumentId);
        Assert.Equal("west", issue.Region);
        Assert.Equal(SyncIssueKind.Missing, issue.Kind);
    }

    [Fact]
    public void Analyze_DocumentMissingFromBaselineRegion_ReportsMissingForBaseline()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(),              // baseline is missing the doc
            ["west"] = Region(MakeProduct("1"))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        Assert.Contains(result.Issues, i => i.Region == "east" && i.Kind == SyncIssueKind.Missing);
    }

    [Fact]
    public void Analyze_DocumentMissingFromMultipleRegions_ReportsOneIssuePerRegion()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"]   = Region(MakeProduct("1")),
            ["west"]   = Region(),
            ["europe"] = Region()
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        Assert.Equal(2, result.Issues.Count);
        Assert.All(result.Issues, i => Assert.Equal(SyncIssueKind.Missing, i.Kind));
    }

    // ---- field-level drift --------------------------------------------------

    [Fact]
    public void Analyze_DifferentName_ReturnsDriftIssueWithNameInDetail()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1", name: "Original")),
            ["west"] = Region(MakeProduct("1", name: "Changed"))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(SyncIssueKind.Drift, issue.Kind);
        Assert.Contains("one or more fields differ", issue.Detail);
    }

    [Fact]
    public void Analyze_DifferentDescription_ReturnsDriftIssue()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1", desc: "Old description")),
            ["west"] = Region(MakeProduct("1", desc: "New description"))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        Assert.Contains(result.Issues, i => i.Kind == SyncIssueKind.Drift && i.Detail.Contains("one or more fields differ"));
    }

    [Fact]
    public void Analyze_DifferentCategory_ReturnsDriftIssueWithCategoryInDetail()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1", category: "Electronics")),
            ["west"] = Region(MakeProduct("1", category: "Accessories"))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        Assert.Contains(result.Issues, i => i.Kind == SyncIssueKind.Drift && i.Detail.Contains("one or more fields differ"));
    }

    [Fact]
    public void Analyze_DifferentPrice_ReturnsDriftIssueWithPriceInDetail()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1", price: 9.99)),
            ["west"] = Region(MakeProduct("1", price: 14.99))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        Assert.Contains(result.Issues, i => i.Kind == SyncIssueKind.Drift && i.Detail.Contains("one or more fields differ"));
    }

    [Fact]
    public void Analyze_DifferentRating_ReturnsDriftIssueWithRatingInDetail()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1", rating: 4.0)),
            ["west"] = Region(MakeProduct("1", rating: 3.5))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        Assert.Contains(result.Issues, i => i.Kind == SyncIssueKind.Drift && i.Detail.Contains("one or more fields differ"));
    }

    [Fact]
    public void Analyze_DifferentTags_ReturnsDriftIssueWithTagsInDetail()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1", tags: new[] { "a" })),
            ["west"] = Region(MakeProduct("1", tags: new[] { "b" }))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        Assert.Contains(result.Issues, i => i.Kind == SyncIssueKind.Drift && i.Detail.Contains("one or more fields differ"));
    }

    [Fact]
    public void Analyze_TagsOrderMatters_SameElementsDifferentOrder_ReturnsDriftIssue()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1", tags: new[] { "a", "b" })),
            ["west"] = Region(MakeProduct("1", tags: new[] { "b", "a" }))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        // Tags.SequenceEqual is order-sensitive, so this is reported as drift
        Assert.False(result.InSync);
        Assert.Contains(result.Issues, i => i.Detail.Contains("one or more fields differ"));
    }

    [Fact]
    public void Analyze_MultipleFieldsDrift_ReportsOneIssuePerDiff()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("1", name: "A", price: 1.0, rating: 4.0)),
            ["west"] = Region(MakeProduct("1", name: "B", price: 2.0, rating: 3.0))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        Assert.Single(result.Issues);   // one issue for the document (any field difference)
        Assert.All(result.Issues, i => Assert.Equal(SyncIssueKind.Drift, i.Kind));
    }

    // ---- three regions -------------------------------------------------------

    [Fact]
    public void Analyze_ThreeRegionsAllInSync_ReturnsInSync()
    {
        var p = MakeProduct("1");
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"]   = Region(MakeProduct("1")),
            ["west"]   = Region(MakeProduct("1")),
            ["europe"] = Region(MakeProduct("1"))
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.True(result.InSync);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Analyze_ThreeRegions_OneRegionMissingDoc_ReportsMissingForThatRegion()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"]   = Region(MakeProduct("1")),
            ["west"]   = Region(MakeProduct("1")),
            ["europe"] = Region()
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        Assert.False(result.InSync);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("europe", issue.Region);
        Assert.Equal(SyncIssueKind.Missing, issue.Kind);
    }

    [Fact]
    public void Analyze_IssuesAreSortedByDocumentId()
    {
        var perRegion = new Dictionary<string, IReadOnlyDictionary<string, Product>>
        {
            ["east"] = Region(MakeProduct("2"), MakeProduct("1")),
            ["west"] = Region()
        };
        var result = SyncAnalyzer.Analyze(perRegion);
        var ids = result.Issues.Select(i => i.DocumentId).ToList();
        Assert.Equal(ids.OrderBy(x => x).ToList(), ids);
    }
}
