using MDH.Shared.DTOs;

namespace MDH.Shared.Contracts;

public interface IInsightClient
{
    Task<InsightResponseDto> GetMarketSummaryAsync(InsightRequestDto request, CancellationToken ct = default);
    Task<InsightResponseDto> ExplainListingAsync(ListingInsightRequestDto request, CancellationToken ct = default);
}
