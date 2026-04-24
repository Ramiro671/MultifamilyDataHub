using Microsoft.EntityFrameworkCore;

namespace MDH.AnalyticsApi.Persistence;

// Read-only view of the warehouse tables (same connection, different context name for separation)
public class AnalyticsDbContext : DbContext
{
    public AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : base(options) { }

    public DbSet<WarehouseListing> DimListings { get; set; }
    public DbSet<WarehouseSubmarket> DimSubmarkets { get; set; }
    public DbSet<WarehouseDailyRent> FactDailyRents { get; set; }
    public DbSet<WarehouseMarketMetrics> FactMarketMetrics { get; set; }
    public DbSet<WarehouseAnomaly> FactAnomalies { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WarehouseListing>().ToTable("dim_listing", "warehouse").HasKey(x => x.ListingId);
        modelBuilder.Entity<WarehouseSubmarket>().ToTable("dim_submarket", "warehouse").HasKey(x => x.SubmarketId);
        modelBuilder.Entity<WarehouseDailyRent>().ToTable("fact_daily_rent", "warehouse").HasKey(x => x.FactId);
        modelBuilder.Entity<WarehouseMarketMetrics>().ToTable("fact_market_metrics", "warehouse").HasKey(x => x.MetricId);
        modelBuilder.Entity<WarehouseAnomaly>().ToTable("fact_anomaly", "warehouse").HasKey(x => x.AnomalyId);

        modelBuilder.Entity<WarehouseListing>()
            .HasOne(l => l.Submarket)
            .WithMany(s => s.Listings)
            .HasForeignKey(l => l.SubmarketId);

        modelBuilder.Entity<WarehouseDailyRent>()
            .HasOne(f => f.Listing)
            .WithMany(l => l.DailyRents)
            .HasForeignKey(f => f.ListingId);

        modelBuilder.Entity<WarehouseAnomaly>()
            .HasOne(a => a.Listing)
            .WithMany(l => l.Anomalies)
            .HasForeignKey(a => a.ListingId);
    }
}

public class WarehouseSubmarket
{
    public int SubmarketId { get; set; }
    public string Name { get; set; } = default!;
    public string State { get; set; } = default!;
    public string Region { get; set; } = default!;
    public ICollection<WarehouseListing> Listings { get; set; } = new List<WarehouseListing>();
    public ICollection<WarehouseMarketMetrics> MarketMetrics { get; set; } = new List<WarehouseMarketMetrics>();
}

public class WarehouseListing
{
    public Guid ListingId { get; set; }
    public string ExternalId { get; set; } = default!;
    public int SubmarketId { get; set; }
    public string StreetAddress { get; set; } = default!;
    public string Unit { get; set; } = default!;
    public int Bedrooms { get; set; }
    public decimal Bathrooms { get; set; }
    public int Sqft { get; set; }
    public string Operator { get; set; } = default!;
    public DateTime FirstSeenAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public bool IsActive { get; set; }
    public WarehouseSubmarket Submarket { get; set; } = default!;
    public ICollection<WarehouseDailyRent> DailyRents { get; set; } = new List<WarehouseDailyRent>();
    public ICollection<WarehouseAnomaly> Anomalies { get; set; } = new List<WarehouseAnomaly>();
}

public class WarehouseDailyRent
{
    public long FactId { get; set; }
    public Guid ListingId { get; set; }
    public DateOnly RentDate { get; set; }
    public decimal AskingRent { get; set; }
    public decimal EffectiveRent { get; set; }
    public decimal Concessions { get; set; }
    public decimal RentPerSqft { get; set; }
    public DateTime LoadedAt { get; set; }
    public WarehouseListing Listing { get; set; } = default!;
}

public class WarehouseMarketMetrics
{
    public long MetricId { get; set; }
    public int SubmarketId { get; set; }
    public int Bedrooms { get; set; }
    public DateOnly MetricDate { get; set; }
    public decimal AvgRent { get; set; }
    public decimal MedianRent { get; set; }
    public decimal RentPerSqft { get; set; }
    public decimal OccupancyEstimate { get; set; }
    public int SampleSize { get; set; }
    public DateTime ComputedAt { get; set; }
    public WarehouseSubmarket Submarket { get; set; } = default!;
}

public class WarehouseAnomaly
{
    public Guid AnomalyId { get; set; }
    public Guid ListingId { get; set; }
    public decimal AskingRent { get; set; }
    public decimal SubmarketAvgRent { get; set; }
    public decimal StdDev { get; set; }
    public decimal ZScore { get; set; }
    public string FlagReason { get; set; } = default!;
    public DateTime DetectedAt { get; set; }
    public bool IsResolved { get; set; }
    public WarehouseListing Listing { get; set; } = default!;
}
