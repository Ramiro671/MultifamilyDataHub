using FluentAssertions;
using Xunit;
using MDH.OrchestrationService.Jobs;
using MDH.OrchestrationService.Persistence;
using MDH.OrchestrationService.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace MDH.OrchestrationService.Tests;

public class DetectAnomaliesJobTests
{
    private static WarehouseDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WarehouseDbContext(opts);
    }

    [Fact]
    public async Task ExecuteAsync_WithNormalRents_NoAnomaliesDetected()
    {
        var db = CreateDb();
        var submarket = new DimSubmarket
        {
            SubmarketId = 1, Name = "Austin", State = "TX", Region = "SW", CreatedAt = DateTime.UtcNow
        };
        db.DimSubmarkets.Add(submarket);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Create 10 listings with rents close together (no outliers)
        for (int i = 1; i <= 10; i++)
        {
            var listingId = Guid.NewGuid();
            db.DimListings.Add(new DimListing
            {
                ListingId = listingId, ExternalId = $"EXT-{i:D3}", SubmarketId = 1,
                StreetAddress = $"{i} Main St", Unit = "#1", Bedrooms = 2,
                Bathrooms = 2, Sqft = 1000, Operator = "Test",
                FirstSeenAt = DateTime.UtcNow, LastUpdatedAt = DateTime.UtcNow
            });
            db.FactDailyRents.Add(new FactDailyRent
            {
                ListingId = listingId, RentDate = today,
                AskingRent = 2000m + i * 10m, EffectiveRent = 2000m + i * 10m,
                Concessions = 0, RentPerSqft = 2.0m, LoadedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();

        var logger = new Mock<ILogger<DetectAnomaliesJob>>();
        var job = new DetectAnomaliesJob(db, logger.Object);
        await job.ExecuteAsync();

        db.FactAnomalies.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithOutlierRent_FlagsAnomaly()
    {
        var db = CreateDb();
        var submarket = new DimSubmarket
        {
            SubmarketId = 2, Name = "Houston", State = "TX", Region = "SW", CreatedAt = DateTime.UtcNow
        };
        db.DimSubmarkets.Add(submarket);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // 9 normal listings around $2000 and 1 extreme outlier at $10000
        for (int i = 1; i <= 9; i++)
        {
            var listingId = Guid.NewGuid();
            db.DimListings.Add(new DimListing
            {
                ListingId = listingId, ExternalId = $"EXT-H{i:D3}", SubmarketId = 2,
                StreetAddress = $"{i} River Rd", Unit = "#1", Bedrooms = 1,
                Bathrooms = 1, Sqft = 700, Operator = "Test",
                FirstSeenAt = DateTime.UtcNow, LastUpdatedAt = DateTime.UtcNow
            });
            db.FactDailyRents.Add(new FactDailyRent
            {
                ListingId = listingId, RentDate = today,
                AskingRent = 2000m, EffectiveRent = 2000m,
                Concessions = 0, RentPerSqft = 2.86m, LoadedAt = DateTime.UtcNow
            });
        }

        var outlierId = Guid.NewGuid();
        db.DimListings.Add(new DimListing
        {
            ListingId = outlierId, ExternalId = "EXT-H-OUTLIER", SubmarketId = 2,
            StreetAddress = "1 Luxury Ave", Unit = "#PH", Bedrooms = 1,
            Bathrooms = 1, Sqft = 700, Operator = "LuxuryOp",
            FirstSeenAt = DateTime.UtcNow, LastUpdatedAt = DateTime.UtcNow
        });
        db.FactDailyRents.Add(new FactDailyRent
        {
            ListingId = outlierId, RentDate = today,
            AskingRent = 10000m, EffectiveRent = 10000m,
            Concessions = 0, RentPerSqft = 14.29m, LoadedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var logger = new Mock<ILogger<DetectAnomaliesJob>>();
        var job = new DetectAnomaliesJob(db, logger.Object);
        await job.ExecuteAsync();

        db.FactAnomalies.Should().NotBeEmpty();
        db.FactAnomalies.First().ListingId.Should().Be(outlierId);
        db.FactAnomalies.First().ZScore.Should().BeGreaterThanOrEqualTo(3);
    }
}
