using MDH.AnalyticsApi.Persistence;
using MDH.Shared.DTOs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MDH.AnalyticsApi.Features.Markets;

public record GetMarketsQuery : IRequest<IReadOnlyList<MarketSummaryDto>>;

public record MarketSummaryDto(
    int SubmarketId,
    string Name,
    string State,
    string Region,
    IReadOnlyList<MarketMetricsDto> LatestMetrics
);

public class GetMarketsQueryHandler : IRequestHandler<GetMarketsQuery, IReadOnlyList<MarketSummaryDto>>
{
    private readonly AnalyticsDbContext _db;

    public GetMarketsQueryHandler(AnalyticsDbContext db) => _db = db;

    public async Task<IReadOnlyList<MarketSummaryDto>> Handle(GetMarketsQuery request, CancellationToken ct)
    {
        // 🔴 BREAKPOINT: query received
        var submarkets = await _db.DimSubmarkets.AsNoTracking().ToListAsync(ct);

        var latestDate = await _db.FactMarketMetrics
            .AsNoTracking()
            .MaxAsync(m => (DateOnly?)m.MetricDate, ct);

        var latestMetrics = latestDate.HasValue
            ? await _db.FactMarketMetrics
                .AsNoTracking()
                .Include(m => m.Submarket)
                .Where(m => m.MetricDate == latestDate.Value)
                .ToListAsync(ct)
            : new List<WarehouseMarketMetrics>();

        return submarkets.Select(s => new MarketSummaryDto(
            s.SubmarketId, s.Name, s.State, s.Region,
            latestMetrics
                .Where(m => m.SubmarketId == s.SubmarketId)
                .Select(m => new MarketMetricsDto(
                    s.Name, m.Bedrooms, m.AvgRent, m.MedianRent,
                    m.RentPerSqft, m.OccupancyEstimate, m.SampleSize,
                    m.MetricDate.ToDateTime(TimeOnly.MinValue)))
                .ToList()
                .AsReadOnly()
        )).ToList().AsReadOnly();
    }
}
