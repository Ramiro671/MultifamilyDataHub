namespace MDH.Shared.DTOs;

public record AnomalyDto(
    Guid AnomalyId,
    Guid ListingId,
    string Submarket,
    int Bedrooms,
    decimal AskingRent,
    decimal SubmarketAvgRent,
    decimal StdDev,
    decimal ZScore,
    string FlagReason,
    DateTime DetectedAt
);
