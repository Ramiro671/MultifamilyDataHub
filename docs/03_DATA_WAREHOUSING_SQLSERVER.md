# 03 — Data Warehousing with SQL Server

> **Study time:** ~40 minutes
> **Prerequisites:** [`02_ARCHITECTURE.md`](./02_ARCHITECTURE.md), [`05_DATA_DICTIONARY.md`](./05_DATA_DICTIONARY.md)

## Why this matters

The SQL Server data warehouse is the center of gravity in this system. Every downstream capability — REST queries, market metrics, anomaly detection, AI summaries — depends on data that was correctly structured in the star schema. If you cannot explain what a fact table is, why the grain matters, and why `AsNoTracking()` is not optional on read paths, you cannot confidently own the data layer.

Smart Apartment Data is fundamentally a data company. Their backend engineers work with multi-million-row rental tables, run aggregation queries across submarket bands, and serve time-series metrics to dashboards. The warehousing concepts in this doc are the ones they use daily.

By the end of this doc you will be able to: (1) define and distinguish OLTP vs OLAP, star schema, grain, dim table, and fact table using concrete examples from this repo; (2) write a T-SQL query that reproduces what `BuildMarketMetricsJob` computes; (3) explain EF Core Code First migrations, `AsNoTracking`, and when to use compiled queries.

---

## OLTP vs OLAP — Why the Schema Shape Matters

**OLTP (Online Transaction Processing)** is optimized for writes. A normalized relational schema (3NF or higher) eliminates data duplication, ensures update consistency, and handles many small concurrent transactions. An e-commerce database storing orders and line items is OLTP. The trade-off: every aggregate query requires JOINs across multiple tables, which at scale becomes expensive.

**OLAP (Online Analytical Processing)** is optimized for reads. A star schema denormalizes data intentionally: dimension attributes are copied into the rows that need them, so GROUP BY queries can run against a single wide table without JOINs. A data warehouse storing daily rent observations is OLAP.

Our warehouse tables are OLAP-shaped:
- `fact_daily_rent` stores one row per listing per day with all the rent metrics inline. An aggregate query like "average rent in Austin for 2BR last month" is a simple `WHERE + GROUP BY` against a single table — no join needed if you add the submarket name as a column, or a single join to `dim_listing` + `dim_submarket` which is tiny (12 rows).
- Row counts: with 1000 listings/tick × 6 ticks/minute × 1440 minutes/day, `fact_daily_rent` can grow to ~8.6M new rows per day locally. The unique constraint `(ListingId, RentDate)` ensures at most one row per listing per calendar day, so the practical growth is bounded by the number of active listings.

---

## Star Schema — Theory and This Repo

A star schema has one central fact table surrounded by dimension tables. The visual resemblance to a star gives the pattern its name.

**Fact table:** Contains measurable, quantitative events at a specific grain. Every row represents one measurable event. In `warehouse.fact_daily_rent` (defined in `src/MDH.OrchestrationService/Persistence/Migrations/20240101000000_InitialWarehouseSchema.cs` lines 63–88), the grain is *one listing × one calendar day*. The columns `AskingRent`, `EffectiveRent`, `Concessions`, and `RentPerSqft` are the measures — the things you aggregate (SUM, AVG, MIN, MAX).

**Dimension table:** Contains descriptive attributes used to slice and filter facts. `warehouse.dim_submarket` (migration lines 19–31) is tiny — only 12 rows for 12 submarkets. `warehouse.dim_listing` (migration lines 33–61) holds the identity and physical characteristics of each unit. These tables change slowly — a listing's address almost never changes.

**Surrogate key:** Every dimension table has an integer or GUID surrogate key (e.g., `SubmarketId INT IDENTITY`, `ListingId UNIQUEIDENTIFIER`). This surrogate key is what the fact table references via FK, not the natural business key. Surrogate keys decouple the fact table from changes in the business key (e.g., if the external operator renumbers a unit).

**Grain:** The grain is the most granular thing one row in a fact table represents. The grain must be stated explicitly before designing the table, because it determines which dimensions you can attach. `fact_daily_rent` grain = one listing × one calendar day. You cannot add an hourly price observation to this table without changing the grain — you would need a new `fact_hourly_rent`.

---

## Slowly Changing Dimensions (SCD)

A Slowly Changing Dimension is a dimension table whose values change infrequently (e.g., a listing's operator changes when the property is sold). The three common approaches:

**SCD Type 1 — Overwrite:** Update the existing row. Lose the old value. Simple, but no history. This is what we use in `CleanListingsJob.cs` line 68: `existing.Operator = rawListing.Operator.Trim()`. If a property changes from "Greystar" to "MAA", the old Greystar value is gone.

**SCD Type 2 — Add a row:** Never update; instead insert a new row with a validity period (`ValidFrom`, `ValidTo`, `IsCurrent`). Query complexity increases (every join needs `WHERE IsCurrent = 1`) but history is preserved. For Smart Apartment Data's use case, knowing when an operator took over a property is analytically valuable. Type 2 would be the production upgrade.

**SCD Type 6 — Hybrid:** Combine Type 1 (current attributes) + Type 2 (historical rows) + Type 3 (previous value column). Used when you need "current value at a glance" and "full history". Complex to maintain.

We use Type 1 by last-write-wins. To upgrade to Type 2 you would add `ValidFrom DATETIME2`, `ValidTo DATETIME2 NULL`, `IsCurrent BIT` columns to `dim_listing` and change the upsert logic to close the current row and insert a new one on any attribute change.

---

## Column-by-Column Walkthrough

**`warehouse.dim_submarket`** — a static seed table. All 12 rows are seeded in the initial migration (lines 147–165 in `20240101000000_InitialWarehouseSchema.cs`). `SubmarketId` is a surrogate int identity. `Region` groups submarkets for portfolio-level rollups ("Southeast/Southwest" covers TX, GA, FL, TN, NC).

**`warehouse.dim_listing`** — the unit identity. `ListingId` is a GUID surrogate. `ExternalId` is the operator-assigned identifier (the natural key from the scraper). The NCI `IX_dim_listing_ExternalId` supports the `WHERE ExternalId = @id` lookup in `CleanListingsJob`. `IsActive BIT` allows soft-deleting listings that disappear from the market without breaking historical fact rows.

**`warehouse.fact_daily_rent`** — the high-volume fact. `FactId BIGINT IDENTITY` is the PK (integer identity is faster to insert and to scan than GUID). The unique composite index `IX_fact_daily_rent_ListingId_RentDate` is a covering NCI that prevents duplicate loads and supports the `WHERE ListingId = @id AND RentDate = @date` pattern in `CleanListingsJob.cs` line 79. `RentPerSqft` is a derived value (`EffectiveRent / Sqft`) stored as a computed-column-equivalent to avoid recomputing it in every aggregation.

**`warehouse.fact_market_metrics`** — the pre-aggregated gold layer. The unique composite NCI `IX_fact_market_metrics_SubmarketId_Bedrooms_MetricDate` enforces one row per submarket × bedroom band × day and supports the `WHERE SubmarketId = @id AND MetricDate BETWEEN @from AND @to` pattern in `GetMarketMetricsQuery.cs`. `OccupancyEstimate` is a rough heuristic derived from rent spread — higher spread implies less pricing power implies lower occupancy. Grain: one submarket × one bedroom band × one calendar day.

**`warehouse.fact_anomaly`** — the flagging table. `ZScore DECIMAL(6,3)` stores the computed z-score to 3 decimal places. `FlagReason NVARCHAR(500)` is a human-readable string assembled by `DetectAnomaliesJob.cs` line 63: `"Rent $2800 is 3.4σ above submarket avg $1950 (bed=2)"`. `IsResolved BIT` enables an analyst workflow where a human marks a false positive as resolved.

---

## Indexing Strategy

SQL Server puts a clustered index on the primary key by default. For IDENTITY columns (BIGINT or INT), this is ideal — new rows always insert at the right edge of the B-tree, no page splits.

**Non-Clustered Indexes (NCIs) we have:**
- `IX_dim_listing_ExternalId` — supports the external_id lookup during ETL upsert in `CleanListingsJob`. Without this, each ETL tick would do a full scan of `dim_listing`.
- `IX_dim_listing_SubmarketId` — supports FK join from fact tables back to dim.
- `IX_fact_daily_rent_ListingId_RentDate` — unique composite; supports idempotent load check and the rent-history-by-listing query pattern.
- `IX_fact_market_metrics_SubmarketId_Bedrooms_MetricDate` — unique composite; supports the `GetMarketMetricsQuery` time-series query.
- `IX_fact_anomaly_ListingId` — supports the join from anomaly to listing in the `GetAnomaliesQuery`.

**What we do not have (and should in production):**
- **Included columns:** Adding `INCLUDE (AskingRent, EffectiveRent)` to `IX_fact_daily_rent_ListingId_RentDate` would make the BuildMarketMetricsJob aggregation a covering query — no key lookup needed.
- **Filtered index:** `WHERE IsActive = 1` on `dim_listing` so queries that filter out inactive listings skip inactive rows entirely.
- **Partitioning:** At billions of rows, `fact_daily_rent` would benefit from range partitioning on `RentDate` with a monthly partition function. Old partitions can be switched out to blob storage as Parquet.

---

## T-SQL Example Queries

### Rent trend for Austin 2BR — last 90 days, weekly average

```sql
SELECT
    DATEPART(year, CAST(f.RentDate AS datetime2)) AS [Year],
    DATEPART(week, CAST(f.RentDate AS datetime2)) AS [Week],
    AVG(f.EffectiveRent)  AS AvgEffectiveRent,
    AVG(f.RentPerSqft)    AS AvgRentPerSqft,
    COUNT(*)              AS SampleSize
FROM warehouse.fact_daily_rent f
JOIN warehouse.dim_listing l  ON l.ListingId  = f.ListingId
JOIN warehouse.dim_submarket s ON s.SubmarketId = l.SubmarketId
WHERE
    s.Name      = 'Austin'
    AND l.Bedrooms = 2
    AND f.RentDate >= CAST(DATEADD(day, -90, GETUTCDATE()) AS date)
GROUP BY
    DATEPART(year,  CAST(f.RentDate AS datetime2)),
    DATEPART(week,  CAST(f.RentDate AS datetime2))
ORDER BY [Year], [Week];
```

Note: we use weekly grouping rather than daily to smooth out the noise from re-generated synthetic data. `AVG` is appropriate here because we want the arithmetic mean for a trend line. If the business question is "what would a typical renter pay?" use `PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY EffectiveRent)` instead — the median is more robust to outliers.

### Top 10 submarkets by rent-per-sqft this month

```sql
SELECT TOP 10
    s.Name                     AS Submarket,
    AVG(m.RentPerSqft)         AS AvgRentPerSqft,
    AVG(m.OccupancyEstimate)   AS AvgOccupancy,
    SUM(m.SampleSize)          AS TotalListings
FROM warehouse.fact_market_metrics m
JOIN warehouse.dim_submarket s ON s.SubmarketId = m.SubmarketId
WHERE m.MetricDate >= CAST(DATEADD(month, -1, GETUTCDATE()) AS date)
GROUP BY s.Name
ORDER BY AvgRentPerSqft DESC;
```

---

## EF Core — Code First Approach

This project uses **Code First** migrations: you define entity classes in C#, EF Core generates SQL. The alternative (DB First) generates C# from an existing database — appropriate when inheriting a legacy schema.

**`WarehouseDbContext`** (`src/MDH.OrchestrationService/Persistence/WarehouseDbContext.cs`) defines the model via Fluent API in `OnModelCreating`. This is preferred over Data Annotations because it keeps infrastructure concerns (column names, index names) out of domain entity classes.

**`AsNoTracking()`:** EF Core's change tracker maintains an in-memory snapshot of every entity retrieved from the database so it can detect modifications and issue `UPDATE` statements on `SaveChanges()`. On read-only queries, this snapshot is pure overhead. `AsNoTracking()` disables it. Every read in `AnalyticsApi` uses it. See `GetMarketsQueryHandler.cs` line 23: `_db.DimSubmarkets.AsNoTracking()`. Without it, loading 10,000 listings allocates 10,000 entity snapshots that are immediately discarded.

**Migrations on startup:** `OrchestrationService.Program.cs` lines 62–64 call `MigrateWithRetryAsync(app.Services)` which applies any pending migrations before the Hangfire server starts. This is appropriate for a small service that owns its schema. In larger production systems you run migrations as a separate Job/InitContainer before traffic starts, to avoid startup race conditions.

---

## Reading an Execution Plan

When a query is slow, you need the execution plan. In SSMS: `Ctrl+M` before running the query. Key nodes to watch:

- **Scan vs Seek:** Index Seek = the optimizer found a covering index and traversed the B-tree to the specific rows. Index Scan = no suitable index, full scan. Scans on large tables kill query performance.
- **Hash Join vs Nested Loop Join:** Nested Loop = one row per outer loop, efficient for small outer sets. Hash Join = build a hash table from the smaller input, probe with the larger — efficient for large-to-large joins. A Hash Join on a 10-row dimension table is a warning sign that the optimizer lacks statistics.
- **Estimated vs Actual rows:** When these diverge significantly (10x), the optimizer made a bad plan. Usually caused by stale statistics. Fix: `UPDATE STATISTICS warehouse.fact_daily_rent`.

---

## Conceptual Scale-Up

If `fact_daily_rent` grew to 5 billion rows:
1. **Range partitioning** on `RentDate` with monthly partitions. Old months switch out to a cold filegroup.
2. **Columnstore index** on `fact_daily_rent` for the aggregation queries. Columnstore compresses the AvgRent/MedianRent columns and processes them in batches — often 10–100x faster than row-store for analytics.
3. **Archival to Parquet** on Azure Blob Storage for data older than 2 years, read via Azure Synapse or Fabric.

---

## Exercise

1. Open `src/MDH.OrchestrationService/Jobs/BuildMarketMetricsJob.cs` lines 24–43. Rewrite the same computation as a single T-SQL query using `GROUP BY SubmarketId, Bedrooms` and `PERCENTILE_CONT` for median.

2. `dim_listing` uses `UNIQUEIDENTIFIER` as the primary key. `fact_daily_rent` uses `BIGINT IDENTITY`. Explain the performance implication of each choice and when you would prefer one over the other.

3. The `fact_anomaly` table has no unique constraint on `(ListingId, DetectedAt::date)`. Look at `DetectAnomaliesJob.cs` line 42 — how does the code prevent duplicate rows without a DB constraint?

4. Open `src/MDH.AnalyticsApi/Features/Listings/SearchListingsQuery.cs` line 33. Why is `AsNoTracking()` called here but not in the EF Core migrations file?

---

## Common mistakes

- **Forgetting `AsNoTracking()` on read paths.** Every entity loaded without `AsNoTracking()` registers a snapshot in the change tracker. A query loading 50,000 listings with tracking enabled allocates 50,000 change-tracker entries and slows down `SaveChanges()` on any subsequent write in the same context scope.

- **Choosing `NEWID()` as the clustered PK on a high-volume fact table.** GUIDs (UUIDs) are random, so inserts scatter across the B-tree causing page splits and fragmentation. Use `BIGINT IDENTITY` for clustered PKs on fact tables. GUIDs are fine as NCIs or for dim tables with low insert rates.

- **Wrong grain definition.** If you decide the grain is "one listing per day" but insert multiple rows per listing per day (e.g., intraday updates), the unique constraint prevents it. Be explicit: the grain defines both the uniqueness constraint and what `GROUP BY` means in downstream queries.

- **Calling `Database.Migrate()` in parallel from multiple service instances.** If two instances of OrchestrationService start simultaneously, both call `MigrateAsync()`. EF Core uses a `__MigrationsHistory` table lock internally, but the retry logic can interfere. The safe pattern is to run migrations as a dedicated init step (Kubernetes init container or ACA startup command) before scaling to N replicas.

- **Keeping `fact_daily_rent` unbounded.** Without a `TTL` or archival policy, the table grows without limit. In production, define a data retention period and a monthly archival job.

---

## Interview Angle — Smart Apartment Data

1. **"What is a star schema?"** — One central fact table surrounded by dimension tables. The fact table holds measurable events at a defined grain (e.g., one listing × one day). Dimension tables hold descriptive attributes used to filter and group. The design is optimized for GROUP BY aggregations — analytics queries — rather than transactional updates.

2. **"What is the grain of fact_daily_rent?"** — One row per unique `(ListingId, RentDate)` combination. The grain is one listing × one calendar day. The unique index `IX_fact_daily_rent_ListingId_RentDate` enforces it at the database level. See migration `20240101000000_InitialWarehouseSchema.cs` line 171.

3. **"Why use AsNoTracking()?"** — EF Core's change tracker snapshots every entity for dirty-check. On read-only queries, those snapshots are pure memory overhead. `AsNoTracking()` skips tracking, reducing memory by the size of the entity × row count. For 10,000 listings this is easily several MB of saved allocations.

4. **"How would you handle SCD Type 2 for dim_listing?"** — Add `ValidFrom DATETIME2 NOT NULL DEFAULT GETUTCDATE()`, `ValidTo DATETIME2 NULL`, `IsCurrent BIT NOT NULL DEFAULT 1`. On any attribute change, `UPDATE SET IsCurrent=0, ValidTo=GETUTCDATE() WHERE IsCurrent=1 AND ExternalId=@id`, then `INSERT` the new row. All fact queries add `JOIN dim_listing l ON l.ListingId = f.ListingId AND l.IsCurrent = 1`.

5. **"What happens if BuildMarketMetricsJob fails halfway?"** — The job transaction is not atomic per-group. Some submarket/bedroom groups would have updated metrics, others would be stale. Hangfire retries the full job. The `FirstOrDefaultAsync + update` pattern is idempotent: re-running replaces the same rows with the same values, so partial success followed by a retry is safe.

6. **30-second talking point:** "The warehouse is a star schema: five tables, two dimensions, three facts. The grain of the primary fact is one listing × one calendar day. Every downstream capability — REST queries, anomaly detection, AI summaries — reads from this curated layer. The design choices I can walk through: surrogate keys for dimension stability, composite unique NCIs to enforce grain and support covering queries, AsNoTracking on all read paths, and Code First migrations applied at service startup with retry for Azure SQL cold-start."

7. **Job requirement proof:** "SQL Server / Data Warehousing" — star schema with 2 dim + 3 fact tables, EF Core Code First migrations, indexed fact tables, T-SQL aggregation in BuildMarketMetricsJob.
