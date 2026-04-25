# 04 — NoSQL Landing Zone with MongoDB

> **Study time:** ~25 minutes
> **Prerequisites:** [`02_ARCHITECTURE.md`](./02_ARCHITECTURE.md), [`03_DATA_WAREHOUSING_SQLSERVER.md`](./03_DATA_WAREHOUSING_SQLSERVER.md)

## Why this matters

Knowing *when* to use a document store vs a relational database is more valuable than knowing the API of either one. Candidates who say "we should use MongoDB" (or "we should use SQL") without justifying the choice signal that they pattern-match on technology rather than reason about requirements. The MongoDB landing zone in this project has a precise reason to exist, and understanding that reason is the answer to "why did you use MongoDB here?"

By the end of this doc you will be able to: (1) explain schema-on-write vs schema-on-read and which scenarios favor each; (2) describe the ETL vs ELT vs medallion landing zone patterns and where this project sits; (3) read and write production-quality MongoDB C# driver code including `InsertManyAsync`, TTL index creation, and aggregation pipeline basics.

---

## Schema-on-Write vs Schema-on-Read

**Schema-on-write (RDBMS):** Every row must conform to the table's column definitions at insert time. This is enforced by the database engine — a row without a required column is rejected. The advantage is that every consumer of the table can rely on the data shape. The disadvantage is that you must know the full schema before the first byte arrives.

**Schema-on-read (document store):** Documents are stored as arbitrary JSON/BSON. The shape is imposed only when you read the document and try to use it. A document can have 5 fields or 50 fields; the database does not care. The advantage is accepting heterogeneous or evolving data without a schema migration. The disadvantage is that every consumer must handle the variability.

**When schema-on-read wins:** Ingesting raw data from external sources where you do not control the schema (web scrapers, third-party APIs, IoT sensors). In this project, the synthetic data always has the same shape — but in a real scraper, each property operator structures their data differently. Some use `numBedrooms`, others use `beds`, others use `bedrooms_count`. MongoDB accepts all of them; SQL Server would require you to decide the canonical column name before ingestion starts.

---

## ETL vs ELT vs Medallion — Where We Sit

**ETL (Extract, Transform, Load):** Data is extracted from the source, transformed in an intermediate step (often in code or a data pipeline tool), and then loaded into the destination in its final form. Old-school enterprise data warehouse pattern. The risk: if the transformation has a bug, you must re-extract from the source.

**ELT (Extract, Load, Transform):** Load the raw data first, then transform in place inside the destination. Enabled by cheap cloud storage and columnar databases (Snowflake, BigQuery, Databricks). The raw data is always available for re-transformation. This is the modern default for cloud data platforms.

**Medallion (this project):** A variant of ELT where the transformation stages are explicit storage tiers:
- Bronze (MongoDB `listings_raw`): raw, unvalidated — ELT's "Load first"
- Silver (SQL `warehouse.dim_listing`, `fact_daily_rent`): cleaned, typed — ELT's "Transform"
- Gold (`fact_market_metrics`, `fact_anomaly`): aggregated, pre-computed

The key insight: raw data in the Bronze layer is never deleted after processing (in production, only after a retention TTL). If CleanListingsJob has a normalization bug, you reset `processed: false` and re-run. No data loss.

---

## BSON Document Anatomy

BSON (Binary JSON) is MongoDB's binary-encoded JSON with additional types. The `listings_raw` collection document shape (from `05_DATA_DICTIONARY.md`):

```json
{
  "_id": { "$oid": "507f1f77bcf86cd799439011" },
  "external_id": "EXT-A3F9B12C7D",
  "submarket": "Austin",
  "street_address": "1234 Lamar Blvd",
  "unit": "#305",
  "bedrooms": 2,
  "bathrooms": 2.0,
  "sqft": 1050,
  "asking_rent": 2350.00,
  "effective_rent": 2200.00,
  "concessions": 150.00,
  "operator": "Greystar",
  "scraped_at": { "$date": "2024-06-15T14:22:11Z" },
  "processed": false,
  "correlation_id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
}
```

`_id` is an `ObjectId` — a 12-byte value encoding timestamp + machine + process + random sequence. It is globally unique and monotonically increasing within a second, which means documents inserted at the same time have adjacent `_id` values and cluster together on disk. This is why MongoDB's default sort order (`_id`) is efficient without an explicit index.

The C# mapping lives in `src/MDH.IngestionService/Models/RawListing.cs`. The `[BsonId]` and `[BsonElement("field_name")]` attributes map C# property names to BSON field names. Without `[BsonElement]`, the C# property name (PascalCase) is used verbatim — correct if you want `AskingRent` in the BSON, problematic if downstream consumers expect `asking_rent`.

---

## MongoDB C# Driver 3.x Patterns

### `IMongoClient` — Singleton Lifetime

```csharp
// src/MDH.IngestionService/Program.cs (and OrchestrationService)
builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConn));
```

`MongoClient` manages a connection pool internally. Registering it as a singleton means the pool is shared across all requests, which is correct. Registering it as transient or scoped creates a new pool on every resolve — a connection leak.

### `InsertManyAsync` — Why Not `InsertOneAsync` in a Loop

`src/MDH.IngestionService/Persistence/RawListingStore.cs` line 28:

```csharp
await _collection.InsertManyAsync(listings, cancellationToken: ct);
```

`InsertManyAsync` sends a single batch write command to MongoDB, inserting all 1000 documents in one network round trip. `InsertOneAsync` in a loop sends 1000 separate commands. At 1ms network latency per round trip, the loop adds 1 second of overhead per batch. The driver also applies the `InsertMany` wire protocol's internal batching (16MB per batch) automatically.

In production, add `new InsertManyOptions { IsOrdered = false }` — the default `IsOrdered: true` stops on the first error. `IsOrdered: false` continues inserting remaining documents even if one fails, which nearly doubles throughput on large batches with occasional duplicates.

### Index Creation in Constructor

```csharp
// src/MDH.IngestionService/Persistence/RawListingStore.cs lines 18-24
var processedIdx = Builders<RawListing>.IndexKeys.Ascending(x => x.Processed);
var externalIdx = Builders<RawListing>.IndexKeys.Ascending(x => x.ExternalId);
_collection.Indexes.CreateMany([
    new CreateIndexModel<RawListing>(processedIdx),
    new CreateIndexModel<RawListing>(externalIdx, new CreateIndexOptions { Unique = false })
]);
```

`CreateMany` is idempotent — if the index already exists with the same definition, MongoDB ignores the call. Calling it in the constructor means every service startup verifies the indexes exist, which is a safe pattern for developer and CI environments. In production you would run index creation as a one-time migration script, not on every startup (for large collections, index creation can take minutes and cause brief query degradation).

---

## Index Types

**Single-field ascending index:** `{ processed: 1 }` — supports `WHERE processed = false` in `CleanListingsJob`. Without it, every job tick scans the entire collection.

**Compound index:** `{ submarket: 1, scraped_at: -1 }` — would support "find recent listings for Austin, sorted newest first". The order of fields in a compound index matters: the leftmost field is used for equality filters, the rightmost for range/sort.

**TTL index:** A special single-field index on a `Date` field. MongoDB automatically deletes documents when `current_time > field_value + expireAfterSeconds`. In production, adding:

```csharp
new CreateIndexModel<RawListing>(
    Builders<RawListing>.IndexKeys.Ascending(x => x.ScrapedAt),
    new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(30) }
)
```

...would automatically purge documents older than 30 days. Without this, `listings_raw` grows indefinitely — critical to address before production.

**Text index:** `{ "$text": { "$search": "Greystar Austin" } }` — full-text search across string fields. Not currently used but relevant if the system needed to search listing descriptions.

---

## Consistency Tradeoffs

MongoDB supports tunable consistency via read/write concerns:

**Write concern `w: 1`** (default): Acknowledge after the primary records the write. Fastest but may lose last write if primary crashes before replication.

**Write concern `w: majority`**: Acknowledge only after a majority of replica set members have the write. Slower but durable. For a landing zone that is batch-reprocessable, `w: 1` is appropriate — if a document is lost before replication, you re-generate it.

**Read concern `local`** (default): Read from the primary, possibly returning uncommitted writes. For a polling consumer like `CleanListingsJob`, this is correct — we want the latest data, not necessarily committed data.

For a financial ledger or order table, you would use `w: majority, readConcern: "majority"` — causal consistency at the cost of ~1–3ms additional latency.

---

## Aggregation Pipeline Example

Find unprocessed listings from the last hour for Austin:

```javascript
db.listings_raw.aggregate([
  // Stage 1: filter to unprocessed + recent
  { $match: {
      processed: false,
      scraped_at: { $gte: new Date(Date.now() - 3600000) },
      submarket: "Austin"
  }},
  // Stage 2: project only needed fields
  { $project: {
      external_id: 1, bedrooms: 1, asking_rent: 1, scraped_at: 1
  }},
  // Stage 3: sort newest first
  { $sort: { scraped_at: -1 } },
  // Stage 4: limit
  { $limit: 500 }
])
```

In C#:

```csharp
var filter = Builders<RawListing>.Filter.And(
    Builders<RawListing>.Filter.Eq(x => x.Processed, false),
    Builders<RawListing>.Filter.Gte(x => x.ScrapedAt, DateTime.UtcNow.AddHours(-1)),
    Builders<RawListing>.Filter.Eq(x => x.Submarket, "Austin")
);
var result = await _collection.Find(filter)
    .SortByDescending(x => x.ScrapedAt)
    .Limit(500)
    .ToListAsync(ct);
```

---

## When NOT to Use MongoDB

MongoDB is the wrong tool when:
- You need multi-document transactions across collections (MongoDB has them but they are expensive and limited)
- You need complex JOINs across many collections (`$lookup` works for simple cases but is not a SQL JOIN)
- You need strict relational integrity with FK constraints (MongoDB has no FK concept)
- Your schema is fully stable and your queries are primarily relational (just use SQL)
- Your team has deep SQL expertise and no MongoDB experience (tool familiarity is a real factor)

The rule of thumb: if you find yourself wanting to run `$lookup` on more than two collections, your data is relational and belongs in SQL.

---

## Exercise

1. Open `src/MDH.IngestionService/Persistence/RawListingStore.cs` line 28. What would happen if you changed `InsertManyAsync(listings)` to a loop of `InsertOneAsync` calls? Estimate the latency difference for a 1000-document batch assuming 1ms network RTT to MongoDB.

2. The `RawListingStore` constructor creates indexes synchronously (returns `void`). Why is this acceptable here but would be a problem in a high-traffic web API?

3. Write the MongoDB aggregation pipeline (JavaScript or C# syntax) that returns the average `asking_rent` per submarket for documents scraped in the last 24 hours where `processed = false`.

4. Explain why `IMongoClient` must be a singleton and `IMongoCollection<T>` can be resolved from it safely on every request.

---

## Common mistakes

- **Registering `MongoClient` as transient or scoped.** Each `new MongoClient(...)` creates a new connection pool. In a web app with 100 concurrent requests, transient registration creates 100 pools, each with a minimum of 1 connection — 100 open connections doing work that 1 pool of 10 could handle.

- **`InsertOneAsync` in a loop.** For batch inserts, always use `InsertManyAsync`. The network round-trip penalty for 1000 single inserts vs one batch insert is typically 1–2 orders of magnitude.

- **No TTL index on the landing zone.** Without it, `listings_raw` grows forever. At 1000 docs/10s = 6M docs/day = ~180M docs/month. At ~500 bytes/doc, that is 90GB/month. The TTL index should be added before production deployment.

- **Using `_id` as a correlation key between MongoDB and SQL.** MongoDB `ObjectId` is MongoDB-internal. Use `external_id` as the business key that crosses the boundary. `ListingId` in SQL is a new GUID surrogate — not the same as `_id` in MongoDB.

- **Not setting `IsOrdered: false` on large batch inserts.** With the default `IsOrdered: true`, a single duplicate-key error stops the entire batch. The remaining 999 valid documents are not inserted. Use `IsOrdered: false` for insert-many operations where partial success is preferable to full rollback.

---

## Interview Angle — Smart Apartment Data

1. **"Why MongoDB for the landing zone?"** — Schema flexibility at ingest time. A real scraper encounters heterogeneous operator formats — some send `numBedrooms`, others `beds`. MongoDB accepts both without a schema migration. The raw data is never lost; if the transformation has a bug, we reset `processed: false` and re-run. SQL Server would force us to decide the schema before the first document arrives.

2. **"What is a TTL index?"** — A MongoDB index on a date field with an `expireAfter` setting. The server periodically scans the index and deletes documents whose date is older than the TTL. It is the MongoDB equivalent of a scheduled cleanup job, but built into the storage engine and runs at low priority without a separate process.

3. **"How would you prevent duplicate listings in MongoDB?"** — Create a unique compound index on `{ external_id: 1, scraped_at: 1 }` (if each scrape at a distinct time is unique) or `{ external_id: 1 }` alone if you only want one document per external ID. Currently we allow duplicates in the landing zone and deduplicate in the ETL step — appropriate for a landing zone where every observation is potentially valuable.

4. **"What is the difference between write concern w:1 and w:majority?"** — `w:1` acknowledges after the primary writes to memory. Fast but risks losing the last write if the primary crashes before replication. `w:majority` waits for quorum confirmation — slower but durable. For a landing zone with re-generatable data, `w:1` is the right tradeoff.

5. **30-second talking point:** "MongoDB serves as the landing zone because it accepts raw data without schema enforcement. The IngestionService inserts 1000 documents per batch using InsertManyAsync — single network round-trip, no per-document overhead. The CleanListingsJob polls for unprocessed documents, normalizes them, and loads them into SQL Server. In production I'd add a TTL index on scraped_at to expire documents after 30 days, preventing unbounded growth."

6. **Job requirement proof:** "NoSQL" — MongoDB 7 used as the raw landing zone with MongoDB.Driver 3.x typed collections, index creation, bulk insert, and bulk update patterns.
