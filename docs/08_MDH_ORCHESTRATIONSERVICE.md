# 08 — MDH.OrchestrationService — ETL and Big Data Orchestration

> **Study time:** ~40 minutes
> **Prerequisites:** [`03_DATA_WAREHOUSING_SQLSERVER.md`](./03_DATA_WAREHOUSING_SQLSERVER.md), [`04_NOSQL_LANDING_ZONE_MONGODB.md`](./04_NOSQL_LANDING_ZONE_MONGODB.md)

## Why this matters

"Big Data Orchestration" is an explicit job requirement in the Smart Apartment Data JD. This is the doc that owns it. Orchestration spans everything from the simple (Hangfire recurring jobs) to the complex (Airflow DAGs, Spark streaming). You need to place Hangfire in the industry landscape — what it is good for, where it falls short, and what you would choose instead at larger scale.

The ETL logic in this service is also the densest backend code in the repo. If you can trace a `RawListingDocument` from MongoDB through `CleanListingsJob` into `fact_daily_rent`, explain the 3-sigma math in `DetectAnomaliesJob`, and explain why EF Core migrations run at startup rather than as a separate step, you have demonstrated data engineering fluency.

By the end of this doc you will be able to: (1) place Hangfire in the orchestration landscape alongside Airflow, ADF, and Databricks; (2) trace the full ETL flow from raw MongoDB document to SQL fact row; (3) derive the 3-sigma anomaly detection math and name its statistical limitations.

---

## Big Data Orchestration — Industry Landscape

"Big Data Orchestration" means scheduling and managing data pipeline jobs. The tools span a wide range:

**Apache Airflow:** Python-native, DAG-based. The industry standard for complex multi-step pipelines with explicit dependency graphs. Requires a dedicated Airflow cluster (scheduler, workers, web server, metadata DB). Right choice: cross-language pipelines, complex DAGs, hundreds of jobs.

**Azure Data Factory:** Cloud-managed, GUI-driven, connector-heavy. No code required for common patterns (Copy Activity, Mapping Data Flow). Right choice: enterprise ETL with non-engineers maintaining pipelines.

**Databricks Workflows / Jobs:** Spark-native. Right choice: processing datasets that don't fit in RAM (terabytes), ML feature engineering, Delta Lake medallion architecture.

**Prefect / Dagster:** Modern Python orchestrators with type-safe DAG definitions and local testing. Growing market share in the cloud-native Python ecosystem.

**Hangfire:** In-process .NET job scheduler backed by a persistent SQL storage. No separate cluster, no separate process — Hangfire runs inside your ASP.NET Core / Worker application. Right choice: small to medium .NET workloads, single ops target, built-in dashboard, DI integration. Not right for: cross-language, complex DAG dependencies, horizontally-scaled workers competing for the same job queue at high throughput.

**Where Hangfire sits here:** The three ETL jobs are simple, sequential, non-branching, .NET-only, and the entire system has a single SQL Server instance. Hangfire is the correct tool. Using Airflow here would be like driving a freight truck to the corner store.

---

## Hangfire Architecture

Hangfire has four components:

1. **Storage** — a SQL Server database (the `hangfire` schema in this project, shared with the warehouse DB) where job records, states, and history are persisted. All Hangfire operations are durable — a server crash and restart resumes all in-flight and pending jobs.

2. **Server** — one or more server processes that poll storage for jobs to execute. `builder.Services.AddHangfireServer()` in `Program.cs` starts the server in this process.

3. **Client** — the part that enqueues jobs. `RecurringJob.AddOrUpdate<CleanListingsJob>(...)` is a client call that writes a recurring job record to storage.

4. **Dashboard** — a read-only (by default) web UI at `/hangfire` showing job history, retry counts, and state transitions. Useful for diagnosing failed jobs.

**Why SQL Server storage:** Using the same database as the warehouse means: (1) one connection string to configure; (2) transactional consistency — if a warehouse write fails, Hangfire can retry; (3) no additional infrastructure to maintain.

---

## The Three Jobs

### `CleanListingsJob` — MongoDB → SQL ETL

**Schedule:** `Cron.Minutely()` — every 1 minute.

**Read pattern:** `IRawListingStore.GetUnprocessedAsync<RawListingDocument>(5000)` — up to 5000 unprocessed MongoDB documents per tick.

**Transform (src/MDH.OrchestrationService/Jobs/CleanListingsJob.cs):**

```csharp
// Dedupe by external_id, take the most recent per ID
var deduped = raw
    .GroupBy(r => r.ExternalId)
    .Select(g => g.OrderByDescending(r => r.ScrapedAt).First())
    .ToList();
```

This handles the case where IngestionService generates two listings with the same `ExternalId` in a single batch (statistically rare but possible). Last-write-wins: the most recently scraped version wins.

**Upsert dim_listing:**

```csharp
var existing = await _db.DimListings
    .FirstOrDefaultAsync(l => l.ExternalId == rawListing.ExternalId);

if (existing == null) {
    // INSERT new listing
} else {
    // UPDATE LastUpdatedAt, StreetAddress, Operator
    existing.LastUpdatedAt = now;
}
await _db.SaveChangesAsync();
```

This is an application-level upsert. SQL Server 2022 has `MERGE` for atomic upsert, but EF Core's `FirstOrDefaultAsync + conditional insert/update` is more readable and works well at the batch sizes we have. At higher throughput, you would switch to `ExecuteSqlRaw("MERGE INTO ...")`.

**Insert fact_daily_rent:**

```csharp
var alreadyLoaded = await _db.FactDailyRents
    .AnyAsync(f => f.ListingId == listingId && f.RentDate == today);
if (!alreadyLoaded && rawListing.Sqft > 0) {
    _db.FactDailyRents.Add(new FactDailyRent { ... });
    await _db.SaveChangesAsync();
}
```

The idempotency check prevents duplicate fact rows. Running the job twice on the same day produces the same result — if the row exists, skip. The `Sqft > 0` guard prevents a divide-by-zero on `EffectiveRent / Sqft` for miscoded listings.

**Write pattern:** `MarkProcessedAsync(processedIds)` — bulk update `processed: true` in MongoDB for all successfully loaded documents.

### `BuildMarketMetricsJob` — Aggregation to Silver/Gold

**Schedule:** `*/5 * * * *` — every 5 minutes.

**Core logic (src/MDH.OrchestrationService/Jobs/BuildMarketMetricsJob.cs lines 24–43):**

```csharp
var rentData = await _db.FactDailyRents
    .Where(f => f.RentDate == today)
    .Join(_db.DimListings, f => f.ListingId, l => l.ListingId,
          (f, l) => new { f.EffectiveRent, f.RentPerSqft, l.SubmarketId, l.Bedrooms })
    .ToListAsync();

var groups = rentData.GroupBy(x => new { x.SubmarketId, x.Bedrooms });

foreach (var group in groups) {
    var rents = group.Select(x => x.EffectiveRent).OrderBy(r => r).ToList();
    var avgRent = rents.Average();
    var medianRent = ComputeMedian(rents);
    // ...upsert fact_market_metrics
}
```

The LINQ `.Join` is translated to a SQL `INNER JOIN` by EF Core. The grouping happens in .NET, not SQL — this is a pragmatic choice for a portfolio project but would be moved to a SQL GROUP BY query in production for large tables.

**Median computation:**

```csharp
private static decimal ComputeMedian(List<decimal> sortedValues) {
    var mid = sortedValues.Count / 2;
    return sortedValues.Count % 2 == 0
        ? (sortedValues[mid - 1] + sortedValues[mid]) / 2
        : sortedValues[mid];
}
```

This is the correct population median formula: for an even-length sorted list, average the two middle values. In T-SQL this is `PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY EffectiveRent)`.

### `DetectAnomaliesJob` — 3-Sigma Anomaly Detection

**Schedule:** `0 * * * *` — hourly.

**Math (src/MDH.OrchestrationService/Jobs/DetectAnomaliesJob.cs lines 44–56):**

```csharp
var mean = rents.Average();
var stdDev = Math.Sqrt(rents.Select(r => (r - mean) * (r - mean)).Sum() / rents.Count);

var zScore = (double)(item.AskingRent - (decimal)mean) / stdDev;
if (Math.Abs(zScore) >= SigmaThreshold) { /* flag */ }
```

**The 3-sigma rule (Empirical Rule):** For a normally distributed dataset, 99.7% of values fall within ±3 standard deviations of the mean. A z-score of |z| ≥ 3.0 therefore occurs with probability ~0.3% by chance. If we have 1000 listings and see one with |z| ≥ 3.0, it is likely a genuine outlier (mistyped rent, fraudulent listing, extraordinary unit).

**Z-score formula:** `z = (x - μ) / σ` where `μ` is the population mean and `σ` is the population standard deviation. We use population stddev (divide by N, not N-1) because we are computing the characteristic of the entire known population of listings in a submarket, not estimating from a sample.

**Limitations of 3-sigma:**
1. **Assumes normal distribution.** Rent distributions have a right tail (luxury units skew the mean high). The 3-sigma rule over-flags normal variance in right-skewed data. **Better:** IQR-based flagging (`Q3 + 1.5 × IQR`) or Isolation Forest.
2. **Masking effect.** A group with many outliers inflates the mean and stddev, making extreme outliers appear less extreme. This is the "swamping" problem.
3. **Minimum sample size.** We skip groups with fewer than 5 listings (line 40: `if (rents.Count < 5) continue`). With N < 5 the stddev is unreliable.
4. **Daily window.** We flag anomalies only on `today`'s data. A listing with a gradually creeping rent increase would not trigger — only sudden outliers.

---

## EF Core Migrations — Startup vs Dedicated Job

`Program.cs` lines 62–64:
```csharp
await MigrateWithRetryAsync(app.Services);
```

**Migrate on startup:** Simple, zero additional deployment step, self-contained. Appropriate when: single service instance, schema is small, migration is fast. Risk: if two instances start simultaneously, both call `MigrateAsync()` — EF Core uses a `__MigrationsHistory` lock but concurrent startup can cause transient errors.

**Dedicated migration job:** Run `dotnet ef database update` as a Kubernetes InitContainer or ACA startup command before the main container starts. The main container only starts after migrations succeed. This is the production pattern at scale because it serializes startup order and avoids race conditions.

**The [Migration] attribute:** Hand-written migration classes require `[Migration("TIMESTAMP_ClassName")]` explicitly. Auto-generated migrations include it automatically. Without the attribute, `IMigrationsAssembly.FindMigrationsInAssembly()` does not discover the class, and `MigrateAsync()` is a no-op. This was one of the four bugs fixed during the Azure deployment (see `AUDIT_REPORT.md`).

**`ConfigureWarnings(w => w.Log(PendingModelChangesWarning))`:** EF Core 9 promotes `PendingModelChangesWarning` to an error by default when the compiled model (entity classes + `OnModelCreating`) does not match the migration snapshot. Downgrading it to a warning with `.Log(...)` instead of throwing is the belt-and-suspenders fix. The root fix was adding `HasData(...)` to the snapshot to match the seed data in `OnModelCreating`.

---

## Conceptual Migration to ADF / Databricks

**To Azure Data Factory:** Extract the three ETL jobs into ADF Pipelines with Copy Activities and Data Flows. Replace `IRawListingStore` with a Cosmos DB ADF connector. Replace SQL upserts with Mapping Data Flows. Gain: GUI, no-code transforms, built-in retry. Lose: code control, unit testability, Polly-style policies.

**To Databricks:** Load `listings_raw` from Cosmos into a Delta Lake Bronze table via Auto Loader. Use Spark SQL for the silver transformation and gold aggregation. Gain: petabyte scale, streaming ingest, ACID Delta transactions. Lose: infrastructure simplicity, .NET-native code, sub-minute job latency.

---

## Exercise

1. Open `CleanListingsJob.cs` lines 54–72. The upsert uses `FirstOrDefaultAsync` followed by a conditional `Add` or update. What would happen if two job instances ran simultaneously and both found `existing == null` for the same `ExternalId`? What constraint would catch the duplicate insert?

2. Open `DetectAnomaliesJob.cs` line 36. The stddev is computed as population stddev (divide by N). Write the formula for sample stddev (divide by N-1). When would you prefer sample stddev here?

3. `BuildMarketMetricsJob` loads `rentData` into .NET memory and groups there. Rewrite the grouping as a T-SQL query using `GROUP BY SubmarketId, Bedrooms` and `AVG(EffectiveRent)` that returns the same results.

4. The `RecurringJob.AddOrUpdate` calls in `Program.cs` run every time the service starts. What happens if you change a cron expression and redeploy? What if you delete a `AddOrUpdate` call — does the job stop running?

---

## Common mistakes

- **Hangfire for complex DAGs.** Hangfire recurring jobs are independent — there is no built-in "run B only if A succeeded." If you need "BuildMarketMetricsJob must succeed before DetectAnomaliesJob runs," Hangfire's Continuations feature (`BackgroundJob.ContinueJobWith`) works for one-off chains but is awkward for recurring pipelines. Use Airflow or Prefect for complex dependency graphs.

- **Not checking min group size before stddev.** `Math.Sqrt(0)` = 0. If all rents in a group are identical (synthetic data edge case), `stdDev = 0` and any division by it produces `NaN` or `Infinity`. The guard `if (stdDev < 0.01) continue` in `DetectAnomaliesJob.cs` line 47 handles this.

- **Running LINQ aggregations on unloaded data.** `.Average()`, `.OrderBy()` etc. in LINQ work fine on `List<T>` in memory. If called on `IQueryable<T>` without `.ToListAsync()`, EF Core attempts to translate them to SQL — which can fail for complex expressions. Always `.ToListAsync()` before in-memory LINQ.

- **Calling `SaveChangesAsync` inside a loop.** `CleanListingsJob` calls `SaveChanges` inside the `foreach` loop — once per listing. For a 5000-record batch, this is 5000+ round trips to SQL. The batch-save pattern (add all entities, then save once) is dramatically faster. This is a known simplification in the demo code.

- **Forgetting `HostAbortedException` guard in the outer catch.** EF design-time tools (`dotnet ef migrations add`) start the app, capture the service provider, then throw `HostAbortedException` to abort. If your outer `catch (Exception ex)` block calls `Environment.Exit(1)`, EF tooling exits with code 1 and reports failure. Guard with `when (ex is not HostAbortedException)`. This was another of the four Azure deployment bugs.

---

## Interview Angle — Smart Apartment Data

1. **"What is Hangfire and when would you use it vs Airflow?"** — Hangfire is an in-process .NET scheduler backed by SQL Server. It is the right choice when: your entire pipeline is .NET, you have a single ops target (no separate cluster), and job counts are in the hundreds. Airflow is better for complex multi-language DAGs, hundreds of concurrent jobs, and when the pipeline team is Python-native.

2. **"Explain the 3-sigma anomaly detection."** — We compute the mean and population standard deviation of asking rents per submarket × bedroom band. A z-score of |z| ≥ 3.0 means the rent is more than 3 standard deviations from the mean — a roughly 0.3% probability event under a normal distribution. Limitations: assumes normality (rent is right-skewed), vulnerable to masking by cluster of outliers, daily window only.

3. **"Why does CleanListingsJob use FirstOrDefaultAsync + conditional insert instead of SQL MERGE?"** — MERGE is atomic but harder to test and debug with EF Core. For the batch sizes here (5000/minute), application-level upsert is fast enough and more readable. At 10x scale I would switch to `ExecuteSqlRaw("MERGE INTO ...")` or use EF Core's `ExecuteUpdate`.

4. **"Why do EF Core migrations run at startup instead of as a separate step?"** — For a single-instance service that owns its schema, startup migration is simpler with no extra deployment step. In production with multiple replicas, I would use an init container to serialize migrations before the main container starts, avoiding race conditions.

5. **"What would you use instead of this system at 10M listings/day?"** — Replace Hangfire with Azure Data Factory or Databricks Workflows. Replace MongoDB polling with Azure Service Bus or Kafka. Replace in-memory LINQ aggregations with T-SQL GROUP BY pushed to the database. Add a columnstore index on `fact_daily_rent` for analytics queries.

6. **30-second talking point:** "OrchestrationService runs three Hangfire jobs: CleanListingsJob pulls unprocessed MongoDB documents every minute and upserts them into the SQL star schema; BuildMarketMetricsJob aggregates rent facts into pre-computed market metrics every 5 minutes; DetectAnomaliesJob applies the 3-sigma rule hourly to flag outliers. Hangfire is the right orchestration tool here because the pipeline is .NET-only, single-DB, and low-complexity. The ETL logic is idempotent — running any job twice produces the same result."

7. **Job requirement proof:** "Big Data Orchestration" — Hangfire recurring jobs, ETL pipeline from MongoDB to SQL Server star schema, 3-sigma anomaly detection, demonstrated knowledge of the orchestration landscape (Airflow, ADF, Databricks, Hangfire).
