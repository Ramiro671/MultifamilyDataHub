namespace MDH.Shared.DTOs;

public record ListingDto(
    Guid Id,
    string ExternalId,
    string Submarket,
    string StreetAddress,
    string Unit,
    int Bedrooms,
    decimal Bathrooms,
    int Sqft,
    decimal AskingRent,
    decimal EffectiveRent,
    decimal Concessions,
    string Operator,
    DateTime ScrapedAt,
    bool IsAnomaly
);
