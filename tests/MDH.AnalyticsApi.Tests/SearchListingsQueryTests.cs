using FluentAssertions;
using Xunit;
using MDH.AnalyticsApi.Features.Listings;
using MDH.AnalyticsApi.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MDH.AnalyticsApi.Tests;

public class SearchListingsQueryTests
{
    private static AnalyticsDbContext CreateDb()
    {
        var opts = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AnalyticsDbContext(opts);

        db.DimSubmarkets.Add(new WarehouseSubmarket
        {
            SubmarketId = 1, Name = "Dallas", State = "TX", Region = "SW"
        });
        db.DimSubmarkets.Add(new WarehouseSubmarket
        {
            SubmarketId = 2, Name = "Miami", State = "FL", Region = "SE"
        });

        for (int i = 1; i <= 5; i++)
        {
            db.DimListings.Add(new WarehouseListing
            {
                ListingId = Guid.NewGuid(),
                ExternalId = $"EXT-DAL-{i:D3}",
                SubmarketId = 1,
                StreetAddress = $"{i} Commerce St",
                Unit = $"#{i}00",
                Bedrooms = i % 3 + 1,
                Bathrooms = 2.0m,
                Sqft = 900 + i * 50,
                Operator = "Lincoln",
                FirstSeenAt = DateTime.UtcNow,
                LastUpdatedAt = DateTime.UtcNow,
                IsActive = true
            });
        }

        db.DimListings.Add(new WarehouseListing
        {
            ListingId = Guid.NewGuid(),
            ExternalId = "EXT-MIA-001",
            SubmarketId = 2,
            StreetAddress = "1 Ocean Dr",
            Unit = "#A",
            Bedrooms = 2,
            Bathrooms = 2.0m,
            Sqft = 1200,
            Operator = "Camden",
            FirstSeenAt = DateTime.UtcNow,
            LastUpdatedAt = DateTime.UtcNow,
            IsActive = true
        });

        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task Handle_FilterBySubmarket_ReturnsOnlyMatchingSubmarket()
    {
        var db = CreateDb();
        var handler = new SearchListingsQueryHandler(db);
        var query = new SearchListingsQuery("Dallas", null, null, null, 1, 25);

        var result = await handler.Handle(query, CancellationToken.None);

        result.Items.Should().AllSatisfy(item => item.Submarket.Should().Be("Dallas"));
        result.TotalCount.Should().Be(5);
    }

    [Fact]
    public async Task Handle_FilterByBedrooms_ReturnsOnlyMatchingBedrooms()
    {
        var db = CreateDb();
        var handler = new SearchListingsQueryHandler(db);
        var query = new SearchListingsQuery("Dallas", 2, null, null, 1, 25);

        var result = await handler.Handle(query, CancellationToken.None);

        result.Items.Should().AllSatisfy(item => item.Bedrooms.Should().Be(2));
    }

    [Fact]
    public async Task Handle_Pagination_ReturnsCorrectPage()
    {
        var db = CreateDb();
        var handler = new SearchListingsQueryHandler(db);
        var query = new SearchListingsQuery(null, null, null, null, 1, 3);

        var result = await handler.Handle(query, CancellationToken.None);

        result.Items.Should().HaveCount(3);
        result.TotalCount.Should().Be(6);
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(3);
    }
}
