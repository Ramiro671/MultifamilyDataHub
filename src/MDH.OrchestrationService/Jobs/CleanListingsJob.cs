using MDH.IngestionService.Models;
using MDH.OrchestrationService.Persistence;
using MDH.OrchestrationService.Persistence.Entities;
using MDH.Shared.Contracts;
using MDH.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace MDH.OrchestrationService.Jobs;

public class CleanListingsJob
{
    private readonly IRawListingStore _rawStore;
    private readonly WarehouseDbContext _db;
    private readonly ILogger<CleanListingsJob> _logger;

    public CleanListingsJob(IRawListingStore rawStore, WarehouseDbContext db, ILogger<CleanListingsJob> logger)
    {
        _rawStore = rawStore;
        _db = db;
        _logger = logger;
    }

    public async Task ExecuteAsync()
    {
        // 🔴 BREAKPOINT: starting ETL batch
        _logger.LogInformation("CleanListingsJob starting ETL batch");

        const int batchSize = 5000;
        var raw = await _rawStore.GetUnprocessedAsync<RawListing>(batchSize);

        if (raw.Count == 0)
        {
            _logger.LogInformation("No unprocessed listings found");
            return;
        }

        _logger.LogInformation("Processing {Count} raw listings", raw.Count);

        // Load submarket map
        var submarketMap = await _db.DimSubmarkets
            .ToDictionaryAsync(s => s.Name.ToLower(), s => s.SubmarketId);

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        // Dedupe by external_id within batch
        var deduped = raw
            .GroupBy(r => r.ExternalId)
            .Select(g => g.OrderByDescending(r => r.ScrapedAt).First())
            .ToList();

        var processedIds = new List<string>();

        foreach (var rawListing in deduped)
        {
            try
            {
                var normalizedSubmarket = Normalize(rawListing.Submarket);
                if (!submarketMap.TryGetValue(normalizedSubmarket, out var submarketId))
                {
                    _logger.LogWarning("Unknown submarket {Submarket}, skipping", rawListing.Submarket);
                    continue;
                }

                // Upsert dim_listing
                var existing = await _db.DimListings
                    .FirstOrDefaultAsync(l => l.ExternalId == rawListing.ExternalId);

                Guid listingId;
                if (existing == null)
                {
                    listingId = Guid.NewGuid();
                    var dimListing = new DimListing
                    {
                        ListingId = listingId,
                        ExternalId = rawListing.ExternalId.Trim(),
                        SubmarketId = submarketId,
                        StreetAddress = rawListing.StreetAddress.Trim(),
                        Unit = rawListing.Unit.Trim(),
                        Bedrooms = rawListing.Bedrooms,
                        Bathrooms = rawListing.Bathrooms,
                        Sqft = rawListing.Sqft,
                        Operator = rawListing.Operator.Trim(),
                        FirstSeenAt = now,
                        LastUpdatedAt = now,
                        IsActive = true
                    };
                    _db.DimListings.Add(dimListing);
                }
                else
                {
                    listingId = existing.ListingId;
                    existing.LastUpdatedAt = now;
                    existing.StreetAddress = rawListing.StreetAddress.Trim();
                    existing.Operator = rawListing.Operator.Trim();
                }

                await _db.SaveChangesAsync();

                // Insert fact_daily_rent (ignore if already exists for today)
                var alreadyLoaded = await _db.FactDailyRents
                    .AnyAsync(f => f.ListingId == listingId && f.RentDate == today);

                if (!alreadyLoaded && rawListing.Sqft > 0)
                {
                    var rentPerSqft = Math.Round(rawListing.EffectiveRent / rawListing.Sqft, 2);
                    _db.FactDailyRents.Add(new FactDailyRent
                    {
                        ListingId = listingId,
                        RentDate = today,
                        AskingRent = rawListing.AskingRent,
                        EffectiveRent = rawListing.EffectiveRent,
                        Concessions = rawListing.Concessions,
                        RentPerSqft = rentPerSqft,
                        LoadedAt = now
                    });
                    await _db.SaveChangesAsync();
                }

                if (rawListing.Id != null) processedIds.Add(rawListing.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process listing {ExternalId}", rawListing.ExternalId);
            }
        }

        if (processedIds.Count > 0)
            await _rawStore.MarkProcessedAsync(processedIds);

        _logger.LogInformation("CleanListingsJob complete: processed {Count} listings", processedIds.Count);
    }

    private static string Normalize(string s) => s.Trim().ToLower();
}
