namespace MDH.Shared.Domain;

public readonly record struct ListingId(Guid Value)
{
    public static ListingId New() => new(Guid.NewGuid());
    public static ListingId From(Guid value) => new(value);
    public static ListingId Parse(string value) => new(Guid.Parse(value));
    public override string ToString() => Value.ToString();
}
