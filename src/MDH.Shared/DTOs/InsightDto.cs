namespace MDH.Shared.DTOs;

public record InsightRequestDto(
    string Submarket,
    DateTime FromDate,
    DateTime ToDate
);

public record ListingInsightRequestDto(
    Guid ListingId
);

public record InsightResponseDto(
    string Summary,
    string? AnomalyExplanation,
    Dictionary<string, object> RawStats,
    DateTime GeneratedAt
);
