using MDH.OrchestrationService.Persistence;
using MDH.OrchestrationService.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace MDH.OrchestrationService.Jobs;

public class DetectAnomaliesJob
{
    private readonly WarehouseDbContext _db;
    private readonly ILogger<DetectAnomaliesJob> _logger;

    private const double SigmaThreshold = 3.0;

    public DetectAnomaliesJob(WarehouseDbContext db, ILogger<DetectAnomaliesJob> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ExecuteAsync()
    {
        // 🔴 BREAKPOINT: starting ETL batch
        _logger.LogInformation("DetectAnomaliesJob starting 3-sigma anomaly detection");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTime.UtcNow;

        // Load today's rents with submarket/bedrooms context
        var rentData = await _db.FactDailyRents
            .Where(f => f.RentDate == today)
            .Join(_db.DimListings,
                f => f.ListingId,
                l => l.ListingId,
                (f, l) => new
                {
                    f.ListingId,
                    f.AskingRent,
                    l.SubmarketId,
                    l.Bedrooms
                })
            .ToListAsync();

        if (rentData.Count == 0)
        {
            _logger.LogInformation("No rent data to analyze for anomalies");
            return;
        }

        var anomalyCount = 0;

        var groups = rentData.GroupBy(x => new { x.SubmarketId, x.Bedrooms });

        foreach (var group in groups)
        {
            var rents = group.Select(x => (double)x.AskingRent).ToList();
            if (rents.Count < 5) continue;

            var mean = rents.Average();
            var stdDev = Math.Sqrt(rents.Select(r => (r - mean) * (r - mean)).Sum() / rents.Count);
            if (stdDev < 0.01) continue;

            foreach (var item in group)
            {
                var zScore = (double)(item.AskingRent - (decimal)mean) / stdDev;

                if (Math.Abs(zScore) < SigmaThreshold) continue;

                // Skip if anomaly already recorded today for this listing
                var alreadyFlagged = await _db.FactAnomalies
                    .AnyAsync(a => a.ListingId == item.ListingId &&
                                   a.DetectedAt >= now.Date);
                if (alreadyFlagged) continue;

                var direction = zScore > 0 ? "above" : "below";
                _db.FactAnomalies.Add(new FactAnomaly
                {
                    AnomalyId = Guid.NewGuid(),
                    ListingId = item.ListingId,
                    AskingRent = item.AskingRent,
                    SubmarketAvgRent = (decimal)mean,
                    StdDev = (decimal)stdDev,
                    ZScore = (decimal)Math.Round(zScore, 3),
                    FlagReason = $"Rent ${item.AskingRent:F0} is {Math.Abs(zScore):F1}σ {direction} submarket avg ${mean:F0} (bed={item.Bedrooms})",
                    DetectedAt = now,
                    IsResolved = false
                });
                anomalyCount++;
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("DetectAnomaliesJob complete: flagged {Count} anomalies", anomalyCount);
    }
}
