using MDH.AnalyticsApi.Persistence;
using MDH.Shared.DTOs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MDH.AnalyticsApi.Features.Listings;

public record GetAnomaliesQuery(string? Submarket, DateTime Since) : IRequest<IReadOnlyList<AnomalyDto>>;

public class GetAnomaliesQueryHandler : IRequestHandler<GetAnomaliesQuery, IReadOnlyList<AnomalyDto>>
{
    private readonly AnalyticsDbContext _db;

    public GetAnomaliesQueryHandler(AnalyticsDbContext db) => _db = db;

    public async Task<IReadOnlyList<AnomalyDto>> Handle(GetAnomaliesQuery request, CancellationToken ct)
    {
        // 🔴 BREAKPOINT: query received
        var query = _db.FactAnomalies
            .AsNoTracking()
            .Include(a => a.Listing)
            .ThenInclude(l => l.Submarket)
            .Where(a => a.DetectedAt >= request.Since);

        if (!string.IsNullOrWhiteSpace(request.Submarket))
            query = query.Where(a => a.Listing.Submarket.Name.ToLower() == request.Submarket.ToLower());

        var results = await query.OrderByDescending(a => a.DetectedAt).Take(500).ToListAsync(ct);

        return results.Select(a => new AnomalyDto(
            a.AnomalyId, a.ListingId,
            a.Listing.Submarket.Name, a.Listing.Bedrooms,
            a.AskingRent, a.SubmarketAvgRent, a.StdDev, a.ZScore,
            a.FlagReason, a.DetectedAt))
            .ToList().AsReadOnly();
    }
}
