namespace MDH.Shared.Domain;

public readonly record struct BedBath(int Bedrooms, decimal Bathrooms)
{
    public override string ToString() => $"{Bedrooms}BR/{Bathrooms}BA";
}
