using Azure.Search.Documents.Indexes;

public sealed class Product
{
    [SimpleField(IsKey = true)]
    public string Id { get; set; } = "";

    [SearchableField(IsFilterable = true, IsSortable = true)]
    public string Name { get; set; } = "";

    [SearchableField]
    public string Description { get; set; } = "";

    [SimpleField(IsFilterable = true, IsFacetable = true, IsSortable = true)]
    public string Category { get; set; } = "";

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public double Price { get; set; }

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public double Rating { get; set; }

    [SearchableField(IsFilterable = true, IsFacetable = true)]
    public string[] Tags { get; set; } = Array.Empty<string>();
}
