using FluentAssertions;
using Xunit;
using MDH.AnalyticsApi.Features.Listings;
using MDH.AnalyticsApi.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MDH.AnalyticsApi.Tests;

public class GetListingByIdTests
{
    private static AnalyticsDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AnalyticsDbContext(opts);

        var submarket = new WarehouseSubmarket
        {
            SubmarketId = 1, Name = "Austin", State = "TX", Region = "SW"
        };
        db.DimSubmarkets.Add(submarket);

        var listingId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        db.DimListings.Add(new WarehouseListing
        {
            ListingId = listingId,
            ExternalId = "EXT-001",
            SubmarketId = 1,
            StreetAddress = "123 Oak St",
            Unit = "#1A",
            Bedrooms = 2,
            Bathrooms = 2.0m,
            Sqft = 1000,
            Operator = "Greystar",
            FirstSeenAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
            IsActive = true
        });
        db.FactDailyRents.Add(new WarehouseDailyRent
        {
            FactId = 1,
            ListingId = listingId,
            RentDate = DateOnly.FromDateTime(DateTime.UtcNow),
            AskingRent = 2000m,
            EffectiveRent = 1950m,
            Concessions = 50m,
            RentPerSqft = 1.95m,
            LoadedAt = DateTime.UtcNow
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task Handle_ExistingListing_ReturnsListingDto()
    {
        var db = CreateDb();
        var handler = new GetListingByIdQueryHandler(db);
        var query = new GetListingByIdQuery(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var result = await handler.Handle(query, CancellationToken.None);

        result.Should().NotBeNull();
        result!.ExternalId.Should().Be("EXT-001");
        result.Submarket.Should().Be("Austin");
        result.Bedrooms.Should().Be(2);
        result.AskingRent.Should().Be(2000m);
    }

    [Fact]
    public async Task Handle_NonExistentId_ReturnsNull()
    {
        var db = CreateDb();
        var handler = new GetListingByIdQueryHandler(db);
        var query = new GetListingByIdQuery(Guid.NewGuid());

        var result = await handler.Handle(query, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Handle_ListingHasRentData_PopulatesRentFields()
    {
        var db = CreateDb();
        var handler = new GetListingByIdQueryHandler(db);
        var query = new GetListingByIdQuery(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        var result = await handler.Handle(query, CancellationToken.None);

        result!.AskingRent.Should().Be(2000m);
        result.EffectiveRent.Should().Be(1950m);
        result.Concessions.Should().Be(50m);
    }
}
