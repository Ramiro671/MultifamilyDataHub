namespace MDH.Shared.Domain;

public static class Submarkets
{
    public static readonly string[] All =
    [
        "Austin", "Houston", "Dallas", "Phoenix", "Atlanta",
        "Denver", "Miami", "Nashville", "Tampa", "Orlando",
        "Raleigh", "Charlotte"
    ];

    public static bool IsValid(string name) =>
        All.Contains(name, StringComparer.OrdinalIgnoreCase);
}
