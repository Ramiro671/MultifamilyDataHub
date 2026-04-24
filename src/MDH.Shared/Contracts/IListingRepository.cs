using MDH.Shared.DTOs;

namespace MDH.Shared.Contracts;

public interface IListingRepository
{
    Task<ListingDto?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ListingDto>> SearchAsync(
        string? submarket, int? bedrooms, decimal? minRent, decimal? maxRent,
        int page, int pageSize, CancellationToken ct = default);
    Task<IReadOnlyList<MarketMetricsDto>> GetMarketMetricsAsync(
        string submarket, DateTime from, DateTime to, CancellationToken ct = default);
    Task<IReadOnlyList<AnomalyDto>> GetAnomaliesAsync(
        string? submarket, DateTime since, CancellationToken ct = default);
    Task<IReadOnlyList<ListingDto>> GetCompsAsync(
        string submarket, int bedrooms, int sqftMin, int sqftMax, CancellationToken ct = default);
}
