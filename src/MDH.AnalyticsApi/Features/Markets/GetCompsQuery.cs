using MDH.AnalyticsApi.Persistence;
using MDH.Shared.DTOs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MDH.AnalyticsApi.Features.Markets;

public record GetCompsQuery(string Submarket, int Bedrooms, int SqftMin, int SqftMax)
    : IRequest<IReadOnlyList<ListingDto>>;

public class GetCompsQueryHandler : IRequestHandler<GetCompsQuery, IReadOnlyList<ListingDto>>
{
    private readonly AnalyticsDbContext _db;

    public GetCompsQueryHandler(AnalyticsDbContext db) => _db = db;

    public async Task<IReadOnlyList<ListingDto>> Handle(GetCompsQuery request, CancellationToken ct)
    {
        // 🔴 BREAKPOINT: query received
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var results = await _db.DimListings
            .AsNoTracking()
            .Include(l => l.Submarket)
            .Include(l => l.DailyRents)
            .Where(l => l.Submarket.Name.ToLower() == request.Submarket.ToLower()
                        && l.Bedrooms == request.Bedrooms
                        && l.Sqft >= request.SqftMin
                        && l.Sqft <= request.SqftMax
                        && l.IsActive)
            .Take(100)
            .ToListAsync(ct);

        var anomalyIds = await _db.FactAnomalies
            .AsNoTracking()
            .Where(a => !a.IsResolved)
            .Select(a => a.ListingId)
            .ToHashSetAsync(ct);

        return results.Select(l =>
        {
            var latestRent = l.DailyRents.OrderByDescending(r => r.RentDate).FirstOrDefault();
            return new ListingDto(
                l.ListingId, l.ExternalId, l.Submarket.Name, l.StreetAddress, l.Unit,
                l.Bedrooms, l.Bathrooms, l.Sqft,
                latestRent?.AskingRent ?? 0, latestRent?.EffectiveRent ?? 0,
                latestRent?.Concessions ?? 0, l.Operator,
                l.LastUpdatedAt, anomalyIds.Contains(l.ListingId));
        }).ToList().AsReadOnly();
    }
}
