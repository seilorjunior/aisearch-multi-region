using Azure.Search.Documents.Indexes;

public sealed record Product
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

    // Records use reference equality for arrays, so provide a custom Equals and GetHashCode
    // to compare Tags by value (order-sensitive), matching the original SequenceEqual behaviour.
    public bool Equals(Product? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Id == other.Id
            && Name == other.Name
            && Description == other.Description
            && Category == other.Category
            && Price == other.Price
            && Rating == other.Rating
            && Tags.SequenceEqual(other.Tags);
    }

    public override int GetHashCode()
    {
        var hc = new HashCode();
        hc.Add(Id);
        hc.Add(Name);
        hc.Add(Description);
        hc.Add(Category);
        hc.Add(Price);
        hc.Add(Rating);
        foreach (var tag in Tags)
            hc.Add(tag);
        return hc.ToHashCode();
    }
}
