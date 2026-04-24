namespace MDH.Shared.DTOs;

public record MarketMetricsDto(
    string Submarket,
    int Bedrooms,
    decimal AvgRent,
    decimal MedianRent,
    decimal RentPerSqft,
    decimal OccupancyEstimate,
    int SampleSize,
    DateTime MetricDate
);
