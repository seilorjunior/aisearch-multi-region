public class ProductTests
{
    [Fact]
    public void DefaultId_IsEmpty()
    {
        var product = new Product();
        Assert.Equal("", product.Id);
    }

    [Fact]
    public void DefaultName_IsEmpty()
    {
        var product = new Product();
        Assert.Equal("", product.Name);
    }

    [Fact]
    public void DefaultTags_IsEmptyArray()
    {
        var product = new Product();
        Assert.NotNull(product.Tags);
        Assert.Empty(product.Tags);
    }

    [Fact]
    public void DefaultPrice_IsZero()
    {
        var product = new Product();
        Assert.Equal(0.0, product.Price);
    }

    [Fact]
    public void DefaultRating_IsZero()
    {
        var product = new Product();
        Assert.Equal(0.0, product.Rating);
    }

    [Fact]
    public void AllProperties_AreSettable()
    {
        var tags = new[] { "tag1", "tag2" };
        var product = new Product
        {
            Id          = "42",
            Name        = "Widget",
            Description = "A useful widget.",
            Category    = "Tools",
            Price       = 19.99,
            Rating      = 4.7,
            Tags        = tags
        };

        Assert.Equal("42", product.Id);
        Assert.Equal("Widget", product.Name);
        Assert.Equal("A useful widget.", product.Description);
        Assert.Equal("Tools", product.Category);
        Assert.Equal(19.99, product.Price);
        Assert.Equal(4.7, product.Rating);
        Assert.Equal(tags, product.Tags);
    }
}
