public class SampleDataTests
{
    [Fact]
    public void Products_HasExpectedCount()
    {
        Assert.Equal(12, SampleData.Products.Count);
    }

    [Fact]
    public void Products_IdsAreAllUnique()
    {
        var ids = SampleData.Products.Select(p => p.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Products_HaveNoNullOrEmptyIds()
    {
        Assert.All(SampleData.Products, p =>
            Assert.False(string.IsNullOrWhiteSpace(p.Id)));
    }

    [Fact]
    public void Products_HaveNoNullOrEmptyNames()
    {
        Assert.All(SampleData.Products, p =>
            Assert.False(string.IsNullOrWhiteSpace(p.Name)));
    }

    [Fact]
    public void Products_HaveNoNullOrEmptyCategories()
    {
        Assert.All(SampleData.Products, p =>
            Assert.False(string.IsNullOrWhiteSpace(p.Category)));
    }

    [Fact]
    public void Products_HavePositivePrices()
    {
        Assert.All(SampleData.Products, p => Assert.True(p.Price > 0));
    }

    [Fact]
    public void Products_HaveRatingsBetweenZeroAndFive()
    {
        Assert.All(SampleData.Products, p =>
        {
            Assert.True(p.Rating >= 0, $"Rating of '{p.Name}' is below 0");
            Assert.True(p.Rating <= 5, $"Rating of '{p.Name}' exceeds 5");
        });
    }

    [Fact]
    public void Products_EachHaveAtLeastOneTag()
    {
        Assert.All(SampleData.Products, p => Assert.NotEmpty(p.Tags));
    }

    [Fact]
    public void Products_ContainExpectedCategories()
    {
        var categories = SampleData.Products.Select(p => p.Category).ToHashSet();
        foreach (var expected in new[] { "Audio", "Computers", "Kitchen", "Furniture", "Outdoors" })
            Assert.Contains(expected, categories);
    }

    [Fact]
    public void Products_IdsAreStringNumbers()
    {
        Assert.All(SampleData.Products, p =>
            Assert.True(int.TryParse(p.Id, out _), $"Id '{p.Id}' is not a numeric string"));
    }
}
