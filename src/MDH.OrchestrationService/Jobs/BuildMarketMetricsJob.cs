using MDH.OrchestrationService.Persistence;
using MDH.OrchestrationService.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MDH.OrchestrationService.Jobs;

public class BuildMarketMetricsJob
{
    private readonly WarehouseDbContext _db;
    private readonly ILogger<BuildMarketMetricsJob> _logger;

    public BuildMarketMetricsJob(WarehouseDbContext db, ILogger<BuildMarketMetricsJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ExecuteAsync()
    {
        // 🔴 BREAKPOINT: starting ETL batch
        _logger.LogInformation("BuildMarketMetricsJob starting aggregations");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;

        // Pull all rent facts for today grouped by submarket and bedrooms
        var rentData = await _db.FactDailyRents
            .Where(f => f.RentDate == today)
            .Join(_db.DimListings,
                f => f.ListingId,
                l => l.ListingId,
                (f, l) => new { f.EffectiveRent, f.RentPerSqft, l.SubmarketId, l.Bedrooms })
            .ToListAsync();

        if (rentData.Count == 0)
        {
            _logger.LogInformation("No rent data found for {Date}", today);
            return;
        }

        var groups = rentData
            .GroupBy(x => new { x.SubmarketId, x.Bedrooms })
            .ToList();

        foreach (var group in groups)
        {
            var rents = group.Select(x => x.EffectiveRent).OrderBy(r => r).ToList();
            var avgRent = rents.Average();
            var medianRent = ComputeMedian(rents);
            var avgRentPerSqft = group.Average(x => x.RentPerSqft);
            var sampleSize = rents.Count;
            // Rough occupancy estimate: inversely correlates with rent spread (higher spread → lower occupancy)
            var occupancyEstimate = Math.Min(0.97m, Math.Max(0.80m, 0.97m - (decimal)(rents.Count > 1
                ? Math.Sqrt((double)rents.Select(r => (r - (decimal)avgRent) * (r - (decimal)avgRent)).Sum() / rents.Count) / (double)avgRent * 0.5
                : 0)));

            // Upsert
            var existing = await _db.FactMarketMetrics
                .FirstOrDefaultAsync(m =>
                    m.SubmarketId == group.Key.SubmarketId &&
                    m.Bedrooms == group.Key.Bedrooms &&
                    m.MetricDate == today);

            if (existing == null)
            {
                _db.FactMarketMetrics.Add(new FactMarketMetrics
                {
                    SubmarketId = group.Key.SubmarketId,
                    Bedrooms = group.Key.Bedrooms,
                    MetricDate = today,
                    AvgRent = Math.Round((decimal)avgRent, 2),
                    MedianRent = Math.Round(medianRent, 2),
                    RentPerSqft = Math.Round(avgRentPerSqft, 2),
                    OccupancyEstimate = Math.Round(occupancyEstimate, 4),
                    SampleSize = sampleSize,
                    ComputedAt = now
                });
            }
            else
            {
                existing.AvgRent = Math.Round((decimal)avgRent, 2);
                existing.MedianRent = Math.Round(medianRent, 2);
                existing.RentPerSqft = Math.Round(avgRentPerSqft, 2);
                existing.OccupancyEstimate = Math.Round(occupancyEstimate, 4);
                existing.SampleSize = sampleSize;
                existing.ComputedAt = now;
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("BuildMarketMetricsJob complete: {Count} submarket/bed combos", groups.Count);
    }

    private static decimal ComputeMedian(List<decimal> sortedValues)
    {
        if (sortedValues.Count == 0) return 0;
        var mid = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 0
            ? (sortedValues[mid - 1] + sortedValues[mid]) / 2
            : sortedValues[mid];
    }
}
