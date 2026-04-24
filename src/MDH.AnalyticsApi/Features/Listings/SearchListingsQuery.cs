using MDH.AnalyticsApi.Persistence;
using MDH.Shared.DTOs;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace MDH.AnalyticsApi.Features.Listings;

public record SearchListingsQuery(
    string? Submarket,
    int? Bedrooms,
    decimal? MinRent,
    decimal? MaxRent,
    int Page,
    int PageSize
) : IRequest<PagedResult<ListingDto>>;

public record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

public class SearchListingsQueryHandler : IRequestHandler<SearchListingsQuery, PagedResult<ListingDto>>
{
    private readonly AnalyticsDbContext _db;

    public SearchListingsQueryHandler(AnalyticsDbContext db) => _db = db;

    public async Task<PagedResult<ListingDto>> Handle(SearchListingsQuery request, CancellationToken ct)
    {
        // 🔴 BREAKPOINT: query received
        var query = _db.DimListings
            .AsNoTracking()
            .Include(l => l.Submarket)
            .Include(l => l.DailyRents)
            .Where(l => l.IsActive);

        if (!string.IsNullOrWhiteSpace(request.Submarket))
            query = query.Where(l => l.Submarket.Name.ToLower() == request.Submarket.ToLower());
        if (request.Bedrooms.HasValue)
            query = query.Where(l => l.Bedrooms == request.Bedrooms.Value);

        // Filter by latest rent
        if (request.MinRent.HasValue || request.MaxRent.HasValue)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var yesterday = today.AddDays(-1);
            var listingIdsWithRent = _db.FactDailyRents
                .Where(r => r.RentDate >= yesterday);

            if (request.MinRent.HasValue)
                listingIdsWithRent = listingIdsWithRent.Where(r => r.AskingRent >= request.MinRent.Value);
            if (request.MaxRent.HasValue)
                listingIdsWithRent = listingIdsWithRent.Where(r => r.AskingRent <= request.MaxRent.Value);

            var filteredIds = listingIdsWithRent.Select(r => r.ListingId);
            query = query.Where(l => filteredIds.Contains(l.ListingId));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(l => l.LastUpdatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(ct);

        var anomalyIds = await _db.FactAnomalies
            .AsNoTracking()
            .Where(a => !a.IsResolved)
            .Select(a => a.ListingId)
            .ToHashSetAsync(ct);

        var dtos = items.Select(l =>
        {
            var latestRent = l.DailyRents.OrderByDescending(r => r.RentDate).FirstOrDefault();
            return new ListingDto(
                l.ListingId, l.ExternalId, l.Submarket.Name, l.StreetAddress, l.Unit,
                l.Bedrooms, l.Bathrooms, l.Sqft,
                latestRent?.AskingRent ?? 0, latestRent?.EffectiveRent ?? 0,
                latestRent?.Concessions ?? 0, l.Operator,
                l.LastUpdatedAt, anomalyIds.Contains(l.ListingId));
        }).ToList().AsReadOnly();

        return new PagedResult<ListingDto>(dtos, total, request.Page, request.PageSize);
    }
}
