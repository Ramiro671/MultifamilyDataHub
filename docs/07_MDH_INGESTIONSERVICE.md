# 07 — MDH.IngestionService — Synthetic Data Ingest

> **Study time:** ~20 minutes
> **Prerequisites:** [`04_NOSQL_LANDING_ZONE_MONGODB.md`](./04_NOSQL_LANDING_ZONE_MONGODB.md), [`06_MDH_SHARED.md`](./06_MDH_SHARED.md)

## Why this matters

IngestionService is intentionally simple — but the patterns it demonstrates are used in every event-driven or batch-ingestion backend. The `BackgroundService` lifecycle, graceful shutdown via `CancellationToken`, structured logging with correlation IDs, and batch insert patterns all appear in production data pipelines at scale. A junior engineer who can explain why `CancellationToken` is passed into every async call, and why batch insert beats loop-insert by 100x, signals production-level thinking.

By the end of this doc you will be able to: (1) trace the full `BackgroundService` lifecycle from startup to graceful shutdown; (2) explain why this service uses Bogus and how to make it deterministic for tests; (3) describe the MongoDB write pattern and its throughput implications.

---

## Role and Explicit Scope

IngestionService simulates a real-world property listing scraper. In a production system it would be replaced by N scrapers (one per operator website) that publish to a message queue (Kafka or Azure Service Bus), which IngestionService would consume. The MongoDB landing zone pattern would be the same; only the data source would change.

This honest scoping matters: if an interviewer asks "how would this scale to 50 operators?" the answer is "replace ListingFaker with N scraper workers publishing to a queue; IngestionService consumes and batch-inserts." The MongoDB write logic in `RawListingStore` is unchanged.

---

## `BackgroundService` Lifecycle

`IngestionWorker` extends `BackgroundService` (`src/MDH.IngestionService/IngestionWorker.cs`):

```csharp
public class IngestionWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // ... do work ...
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }
}
```

The .NET runtime calls `ExecuteAsync` once when the host starts. The method is expected to run indefinitely until `stoppingToken` is cancelled. When the host receives SIGTERM (container stop, Ctrl+C), it calls `IHostedService.StopAsync()`, which cancels `stoppingToken`. The `while (!stoppingToken.IsCancellationRequested)` loop exits cleanly, and `Task.Delay(..., stoppingToken)` throws `OperationCanceledException` (which `BackgroundService` catches and swallows) — so the service does not wait out a full 10-second sleep when stopping.

**Why pass `stoppingToken` everywhere:** Every `await` that takes a `CancellationToken` will unblock immediately when the token is cancelled. If you omit the token from `InsertManyAsync(listings, cancellationToken: ct)`, the MongoDB insert will run to completion even during shutdown — potentially holding up container termination by several seconds. On Azure Container Apps, containers that do not exit within the termination grace period are killed hard, which can corrupt in-flight inserts.

**`StartAsync` vs `ExecuteAsync`:** You rarely override `StartAsync` directly. `BackgroundService.StartAsync` fires `ExecuteAsync` as a background task and returns immediately. The host startup is not blocked by the long-running loop — the host starts serving health checks (`/health`) while `ExecuteAsync` runs concurrently.

---

## Configuration — `IOptions<T>` Pattern

```csharp
// Reading in IngestionWorker.cs
var intervalSeconds = _config.GetValue<int>("Ingestion:IntervalSeconds", 10);
var batchSize       = _config.GetValue<int>("Ingestion:BatchSize", 1000);
```

The strongly-typed alternative would be:

```csharp
// In Program.cs: builder.Services.Configure<IngestionOptions>(config.GetSection("Ingestion"));
// In Worker: public IngestionWorker(IOptions<IngestionOptions> opts, ...) { _opts = opts.Value; }
```

`IOptions<T>` is the production preference because it is testable — you can pass `Options.Create(new IngestionOptions { BatchSize = 5 })` in tests without setting environment variables. The current `GetValue` approach works fine and is slightly less ceremony.

Cloud override: `appsettings.Production.json` sets `BatchSize: 50` and `IntervalSeconds: 60` to stay within Cosmos DB free tier (1000 RU/s × 50 docs × ~8 RU/insert ≈ 400 RU/tick, well within budget). This is environment-aware configuration without code changes.

---

## Bogus Library — Synthetic Data Generation

`src/MDH.IngestionService/Generation/ListingFaker.cs` uses [Bogus](https://github.com/bchavez/Bogus):

```csharp
var faker = new Faker<RawListing>()
    .RuleFor(x => x.Submarket, f => f.PickRandom(Submarkets.All))
    .RuleFor(x => x.Bedrooms, f => f.PickRandom(0, 1, 1, 2, 2, 2, 3, 3, 4))
    .RuleFor(x => x.AskingRent, (f, r) => Math.Round(
        (r.Bedrooms + 1) * f.Random.Decimal(700m, 1200m) + f.Random.Decimal(-150m, 150m), 2))
    // ...
```

`Faker<T>` is a fluent builder. Each `.RuleFor(expression, generator)` call defines how to produce that property. The `(f, r)` overload gives access to both the `Faker` instance (`f`) and the partially-built object (`r`), enabling dependent rules: `AskingRent` depends on `Bedrooms`.

The bedroom distribution `f.PickRandom(0, 1, 1, 2, 2, 2, 3, 3, 4)` is weighted: 2BR appears three times, so it is selected with 3/9 probability. This approximates the real multifamily unit mix where 2BRs dominate.

**Deterministic Bogus for tests:** `new Faker<T>().UseSeed(1234)` produces the same data every run. See the tests in `tests/MDH.OrchestrationService.Tests/` — they use a seeded Bogus to generate predictable test data for the anomaly detector math.

---

## MongoDB Write Pattern

```csharp
// src/MDH.IngestionService/Persistence/RawListingStore.cs
public async Task InsertManyAsync(IEnumerable<object> documents, CancellationToken ct = default)
{
    var listings = documents.Cast<RawListing>().ToList();
    await _collection.InsertManyAsync(listings, cancellationToken: ct);
    // 🔴 BREAKPOINT: raw batch persisted
    _logger.LogInformation("Persisted {Count} raw listings to MongoDB", listings.Count);
}
```

1000 documents → 1 MongoDB write command → ~1 network round trip. Without bulk insert, 1000 `InsertOneAsync` calls would take ~1000 × network RTT. On a local network that is ~10ms (1000 × 0.01ms RTT ≈ 10ms total for bulk, 1000 × 0.01ms ≈ 10ms per call × 1000 = 10 seconds for loop).

In production, adding `new InsertManyOptions { IsOrdered = false }` improves throughput further by allowing the driver to parallelize the batch internally and not stop on a single duplicate key error.

---

## Serilog — Structured Logging with Correlation IDs

```csharp
// IngestionWorker.cs
var correlationId = Guid.NewGuid().ToString();
_logger.LogInformation("Starting ingestion tick {CorrelationId}", correlationId);
// ...
_logger.LogInformation("Ingestion tick complete. CorrelationId={CorrelationId} Count={Count}",
    correlationId, batch.Count);
```

The `{CorrelationId}` is a named parameter in a structured log message. Serilog serializes this as a key-value pair in JSON — not just a formatted string. In Log Analytics (or any Serilog sink that outputs JSON), you can filter:

```
| where Properties.CorrelationId == "a1b2c3d4-..."
```

This traces everything that happened in a single ingestion tick — from the batch generated by the Faker through the MongoDB insert through to the ETL job that processed those documents (which logs the same `CorrelationId` if you pass it through). Without structured logging, you would have to regex-parse free-text log lines.

---

## Health Check Wiring

IngestionService exposes `/health` on port 5010. The health check configuration is minimal:

```csharp
// src/MDH.IngestionService/Program.cs
builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"]);
```

Liveness (`/health`, `Predicate = _ => false`) returns 200 always — if the process is running, liveness passes. Readiness (`/health/ready`) probes MongoDB. Azure Container Apps checks liveness every 30 seconds and readiness before routing traffic. See [`14_CLOUD_FUNDAMENTALS.md`](./14_CLOUD_FUNDAMENTALS.md) for the full liveness vs readiness explanation.

---

## 🔴 BREAKPOINT Walkthrough

| File | Comment | What to inspect |
|---|---|---|
| `src/MDH.IngestionService/Persistence/RawListingStore.cs:33` | `🔴 BREAKPOINT: raw batch persisted` | Inspect `listings.Count` (should be 1000 locally), `listings[0]` to see BSON shape, Mongo `_collection` |

Set a conditional breakpoint: `listings.Count < 100` to pause only on undersized batches (useful for debugging partial-batch scenarios).

---

## Failure Modes

**MongoDB unreachable:** `InsertManyAsync` throws `MongoConnectionException`. `IngestionWorker.cs` catches `Exception when (!stoppingToken.IsCancellationRequested)` (line 31), logs the error, and retries on the next tick. The service continues running — one failed tick does not kill the process.

**BSON serialization error:** If a `RawListing` property has a type that MongoDB.Driver cannot serialize (e.g., an unsupported struct), the insert fails. Fix: add `[BsonRepresentation(BsonType.String)]` or implement a custom `IBsonSerializer`.

**CancellationToken honored:** `Task.Delay(interval, stoppingToken)` throws `OperationCanceledException` when the token is cancelled (during shutdown). `BackgroundService.ExecuteAsync` catches this and completes the task cleanly, allowing the host to stop gracefully.

---

## Exercise

1. Open `src/MDH.IngestionService/IngestionWorker.cs` line 31. The catch clause is `catch (Exception ex) when (!stoppingToken.IsCancellationRequested)`. Why does the `when` guard exist? What would happen if you removed it?

2. `ListingFaker.GenerateBatch` is `static`. Write a test that calls `GenerateBatch(50, "test-corr")` with a seeded `Faker` and asserts that all 50 listings have `Processed == false`.

3. The `BatchSize` defaults to 1000 locally but 50 in production (`appsettings.Production.json`). Why is reducing batch size the right lever for Cosmos DB free tier instead of increasing `IntervalSeconds`?

4. What would happen to in-flight MongoDB inserts if the container received SIGKILL instead of SIGTERM (e.g., Azure force-killed it)? How does MongoDB handle partially written batches?

---

## Common mistakes

- **Not passing `CancellationToken` to async calls.** If `InsertManyAsync` does not receive the token, the insert runs to completion even during shutdown. Container termination grace period (typically 30s) expires, the runtime sends SIGKILL, and you get a partially written batch with no error logged.

- **Throwing in `ExecuteAsync` without catching.** An unhandled exception in `ExecuteAsync` causes the host to consider the hosted service failed. Whether this shuts down the whole process depends on `BackgroundServiceExceptionBehavior`. In .NET 6+, unhandled exceptions in `BackgroundService` log a fatal error and crash the host by default.

- **Using `Task.Run` inside `BackgroundService`.** Fire-and-forget `Task.Run` from `ExecuteAsync` creates unobserved tasks. Exceptions in those tasks are silently swallowed (or in some configurations, crash the process). Use `await` throughout.

- **Reading config in the constructor.** If you read `_config.GetValue<int>("BatchSize")` in the constructor, you get the value at startup time only. For dynamically changing config (e.g., `IOptionsMonitor<T>`), read in the loop. For static config like batch size, the constructor or `ExecuteAsync` entry is fine.

- **Bogus without `UseSeed` in tests.** Without a seed, each test run produces different data. Tests that assert specific field values (e.g., `AskingRent > 0`) pass, but tests that assert exact values or ordering will fail non-deterministically. Always seed Bogus in test code.

---

## Interview Angle — Smart Apartment Data

1. **"How does BackgroundService handle graceful shutdown?"** — `IHost` cancels the `stoppingToken` on SIGTERM. The `while (!stoppingToken.IsCancellationRequested)` loop exits at the next iteration. `Task.Delay(interval, stoppingToken)` unblocks immediately when the token fires, so shutdown is fast (does not wait for the full sleep interval). Every async call in the loop receives the token so they abort promptly.

2. **"Why 1000 documents per batch locally but 50 in production?"** — Cosmos DB free tier is capped at 1000 RU/s. Each insert is ~8 RU, so 50 docs × 8 RU/doc = 400 RU/tick, with 60s between ticks, safely within budget. Locally, SQL Server and local MongoDB have no RU cap, so 1000/10s is fine. The configuration is environment-aware without code changes.

3. **"How would you scale to 50 operator scrapers?"** — Replace `ListingFaker` with N scraper workers. Each scraper publishes to a partition of an Azure Service Bus queue or Kafka topic (partition by operator or submarket). IngestionService becomes a queue consumer that batch-inserts messages into MongoDB. The MongoDB write pattern (`InsertManyAsync`) is unchanged.

4. **"What structured logging library do you use and why?"** — Serilog, because it captures named parameters as structured key-value pairs, not just formatted strings. This enables Log Analytics queries like `where Properties.CorrelationId == '...'` to trace a single tick across all log events, which is far faster to query than regex-parsing free-text.

5. **30-second talking point:** "IngestionService is a BackgroundService worker that generates 1000 synthetic listings every 10 seconds using the Bogus library and writes them to MongoDB in a single batch insert. Graceful shutdown is handled via CancellationToken passed to every async call. In production the BatchSize drops to 50 to stay within Cosmos DB free tier. The real architectural role is source emulator — in production this would be replaced by scraper workers publishing to a queue."

6. **Job requirement proof:** "Large datasets / data pipelines" — 1000 docs/tick, batch write pattern, configurable throughput, structured correlation-ID logging across service boundaries.
