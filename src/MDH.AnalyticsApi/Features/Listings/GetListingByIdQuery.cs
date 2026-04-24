using MDH.AnalyticsApi.Persistence;
using MDH.Shared.DTOs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MDH.AnalyticsApi.Features.Listings;

public record GetListingByIdQuery(Guid Id) : IRequest<ListingDto?>;

public class GetListingByIdQueryHandler : IRequestHandler<GetListingByIdQuery, ListingDto?>
{
    private readonly AnalyticsDbContext _db;

    public GetListingByIdQueryHandler(AnalyticsDbContext db) => _db = db;

    public async Task<ListingDto?> Handle(GetListingByIdQuery request, CancellationToken ct)
    {
        // 🔴 BREAKPOINT: query received
        var listing = await _db.DimListings
            .AsNoTracking()
            .Include(l => l.Submarket)
            .Include(l => l.DailyRents)
            .Include(l => l.Anomalies)
            .FirstOrDefaultAsync(l => l.ListingId == request.Id, ct);

        if (listing == null) return null;

        var latestRent = listing.DailyRents.OrderByDescending(r => r.RentDate).FirstOrDefault();
        var isAnomaly = listing.Anomalies.Any(a => !a.IsResolved);

        return new ListingDto(
            listing.ListingId, listing.ExternalId, listing.Submarket.Name,
            listing.StreetAddress, listing.Unit, listing.Bedrooms, listing.Bathrooms,
            listing.Sqft, latestRent?.AskingRent ?? 0, latestRent?.EffectiveRent ?? 0,
            latestRent?.Concessions ?? 0, listing.Operator, listing.LastUpdatedAt, isAnomaly);
    }
}
