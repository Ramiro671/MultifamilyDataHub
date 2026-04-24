using MDH.AnalyticsApi.Persistence;
using MDH.Shared.DTOs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MDH.AnalyticsApi.Features.Markets;

public record GetMarketMetricsQuery(string Submarket, DateTime From, DateTime To)
    : IRequest<IReadOnlyList<MarketMetricsDto>>;

public class GetMarketMetricsQueryHandler : IRequestHandler<GetMarketMetricsQuery, IReadOnlyList<MarketMetricsDto>>
{
    private readonly AnalyticsDbContext _db;

    public GetMarketMetricsQueryHandler(AnalyticsDbContext db) => _db = db;

    public async Task<IReadOnlyList<MarketMetricsDto>> Handle(GetMarketMetricsQuery request, CancellationToken ct)
    {
        // 🔴 BREAKPOINT: query received
        var fromDate = DateOnly.FromDateTime(request.From);
        var toDate = DateOnly.FromDateTime(request.To);

        var metrics = await _db.FactMarketMetrics
            .AsNoTracking()
            .Include(m => m.Submarket)
            .Where(m => m.Submarket.Name.ToLower() == request.Submarket.ToLower()
                        && m.MetricDate >= fromDate
                        && m.MetricDate <= toDate)
            .OrderBy(m => m.MetricDate)
            .ThenBy(m => m.Bedrooms)
            .ToListAsync(ct);

        return metrics.Select(m => new MarketMetricsDto(
            m.Submarket.Name, m.Bedrooms, m.AvgRent, m.MedianRent,
            m.RentPerSqft, m.OccupancyEstimate, m.SampleSize,
            m.MetricDate.ToDateTime(TimeOnly.MinValue)))
            .ToList().AsReadOnly();
    }
}
