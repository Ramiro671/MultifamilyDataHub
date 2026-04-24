using MDH.OrchestrationService.Persistence.Entities;
using MDH.Shared.Domain;
using Microsoft.EntityFrameworkCore;

namespace MDH.OrchestrationService.Persistence;

public class WarehouseDbContext : DbContext
{
    public WarehouseDbContext(DbContextOptions<WarehouseDbContext> options) : base(options) { }

    public DbSet<DimSubmarket> DimSubmarkets { get; set; }
    public DbSet<DimListing> DimListings { get; set; }
    public DbSet<FactDailyRent> FactDailyRents { get; set; }
    public DbSet<FactMarketMetrics> FactMarketMetrics { get; set; }
    public DbSet<FactAnomaly> FactAnomalies { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<FactDailyRent>()
            .HasIndex(x => new { x.ListingId, x.RentDate })
            .IsUnique();

        modelBuilder.Entity<FactMarketMetrics>()
            .HasIndex(x => new { x.SubmarketId, x.Bedrooms, x.MetricDate })
            .IsUnique();

        modelBuilder.Entity<DimListing>()
            .HasIndex(x => x.ExternalId);

        // Seed submarkets
        var submarkets = Submarkets.All.Select((name, idx) => new DimSubmarket
        {
            SubmarketId = idx + 1,
            Name = name,
            State = GetState(name),
            Region = GetRegion(name),
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        }).ToArray();

        modelBuilder.Entity<DimSubmarket>().HasData(submarkets);
    }

    private static string GetState(string submarket) => submarket switch
    {
        "Austin" or "Houston" or "Dallas" => "TX",
        "Phoenix" => "AZ",
        "Atlanta" => "GA",
        "Denver" => "CO",
        "Miami" or "Tampa" or "Orlando" => "FL",
        "Nashville" => "TN",
        "Raleigh" or "Charlotte" => "NC",
        _ => "US"
    };

    private static string GetRegion(string submarket) => submarket switch
    {
        "Austin" or "Houston" or "Dallas" or "Nashville" or "Atlanta"
        or "Raleigh" or "Charlotte" or "Miami" or "Tampa" or "Orlando" => "Southeast/Southwest",
        "Phoenix" or "Denver" => "Mountain West",
        _ => "Other"
    };
}
