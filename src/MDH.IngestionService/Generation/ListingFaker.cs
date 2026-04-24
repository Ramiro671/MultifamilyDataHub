using Bogus;
using MDH.IngestionService.Models;
using MDH.Shared.Domain;

namespace MDH.IngestionService.Generation;

public static class ListingFaker
{
    private static readonly string[] Operators =
    [
        "Greystar", "EquityResidential", "MAA", "Camden", "UDR",
        "NMI", "Lincoln", "Bozzuto", "Alliance", "WinnCompanies"
    ];

    public static IReadOnlyList<RawListing> GenerateBatch(int count, string correlationId)
    {
        var faker = new Faker<RawListing>()
            .RuleFor(x => x.ExternalId, f => $"EXT-{f.Random.AlphaNumeric(10).ToUpper()}")
            .RuleFor(x => x.Submarket, f => f.PickRandom(Submarkets.All))
            .RuleFor(x => x.StreetAddress, f => f.Address.StreetAddress())
            .RuleFor(x => x.Unit, f => $"#{f.Random.Int(100, 999)}")
            .RuleFor(x => x.Bedrooms, f => f.PickRandom(0, 1, 1, 2, 2, 2, 3, 3, 4))
            .RuleFor(x => x.Bathrooms, (f, r) => r.Bedrooms switch
            {
                0 => 1.0m,
                1 => f.PickRandom(1.0m, 1.5m),
                2 => f.PickRandom(1.0m, 2.0m),
                3 => f.PickRandom(2.0m, 2.5m),
                _ => f.PickRandom(2.0m, 3.0m)
            })
            .RuleFor(x => x.Sqft, (f, r) => r.Bedrooms switch
            {
                0 => f.Random.Int(400, 650),
                1 => f.Random.Int(600, 900),
                2 => f.Random.Int(900, 1300),
                3 => f.Random.Int(1200, 1800),
                _ => f.Random.Int(1600, 2400)
            })
            .RuleFor(x => x.AskingRent, (f, r) => Math.Round(
                (r.Bedrooms + 1) * f.Random.Decimal(700m, 1200m) + f.Random.Decimal(-150m, 150m), 2))
            .RuleFor(x => x.Concessions, f => Math.Round(f.Random.Decimal(0m, 200m), 2))
            .RuleFor(x => x.EffectiveRent, (f, r) => Math.Round(r.AskingRent - r.Concessions, 2))
            .RuleFor(x => x.Operator, f => f.PickRandom(Operators))
            .RuleFor(x => x.ScrapedAt, f => f.Date.RecentOffset(1).UtcDateTime)
            .RuleFor(x => x.Processed, _ => false)
            .RuleFor(x => x.CorrelationId, _ => correlationId);

        return faker.Generate(count);
    }
}
