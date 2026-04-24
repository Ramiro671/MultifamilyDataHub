using FluentAssertions;
using Xunit;
using MDH.OrchestrationService.Jobs;
using MDH.OrchestrationService.Persistence;
using MDH.OrchestrationService.Persistence.Entities;
using MDH.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace MDH.OrchestrationService.Tests;

public class CleanListingsJobTests
{
    private static WarehouseDbContext CreateInMemoryDb()
    {
        var opts = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new WarehouseDbContext(opts);

        // Seed submarkets
        db.DimSubmarkets.Add(new DimSubmarket
        {
            SubmarketId = 1, Name = "Austin", State = "TX", Region = "Southwest",
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task ExecuteAsync_WithNoUnprocessedListings_LogsAndReturns()
    {
        var db = CreateInMemoryDb();
        var rawStore = new Mock<IRawListingStore>();
        rawStore.Setup(s => s.GetUnprocessedAsync<RawListingDocument>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RawListingDocument>().AsReadOnly());

        var logger = new Mock<ILogger<CleanListingsJob>>();
        var job = new CleanListingsJob(rawStore.Object, db, logger.Object);

        await job.ExecuteAsync();

        db.DimListings.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithValidListing_CreatesWarehouseEntry()
    {
        var db = CreateInMemoryDb();
        var rawDoc = new RawListingDocument
        {
            Id = "507f1f77bcf86cd799439011",
            ExternalId = "EXT-TEST001",
            Submarket = "Austin",
            StreetAddress = "100 Main St",
            Unit = "#101",
            Bedrooms = 2,
            Bathrooms = 2.0m,
            Sqft = 1000,
            AskingRent = 2000m,
            EffectiveRent = 1900m,
            Concessions = 100m,
            Operator = "Greystar",
            ScrapedAt = DateTime.UtcNow,
            Processed = false
        };

        var rawStore = new Mock<IRawListingStore>();
        rawStore.Setup(s => s.GetUnprocessedAsync<RawListingDocument>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RawListingDocument> { rawDoc }.AsReadOnly());
        rawStore.Setup(s => s.MarkProcessedAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<CleanListingsJob>>();
        var job = new CleanListingsJob(rawStore.Object, db, logger.Object);

        await job.ExecuteAsync();

        db.DimListings.Should().HaveCount(1);
        db.DimListings.First().ExternalId.Should().Be("EXT-TEST001");
        db.DimListings.First().Bedrooms.Should().Be(2);
        db.FactDailyRents.Should().HaveCount(1);
        db.FactDailyRents.First().AskingRent.Should().Be(2000m);
    }

    [Fact]
    public async Task ExecuteAsync_WithUnknownSubmarket_SkipsListing()
    {
        var db = CreateInMemoryDb();
        var rawDoc = new RawListingDocument
        {
            Id = "507f1f77bcf86cd799439012",
            ExternalId = "EXT-TEST002",
            Submarket = "NonExistentCity",
            StreetAddress = "999 Unknown St",
            Unit = "#1",
            Bedrooms = 1,
            Bathrooms = 1.0m,
            Sqft = 700,
            AskingRent = 1500m,
            EffectiveRent = 1500m,
            Concessions = 0m,
            Operator = "TestOp",
            ScrapedAt = DateTime.UtcNow,
            Processed = false
        };

        var rawStore = new Mock<IRawListingStore>();
        rawStore.Setup(s => s.GetUnprocessedAsync<RawListingDocument>(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<RawListingDocument> { rawDoc }.AsReadOnly());

        var logger = new Mock<ILogger<CleanListingsJob>>();
        var job = new CleanListingsJob(rawStore.Object, db, logger.Object);

        await job.ExecuteAsync();

        db.DimListings.Should().BeEmpty();
    }
}
