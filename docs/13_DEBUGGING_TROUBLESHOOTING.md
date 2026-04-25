# 13 — Debugging and Troubleshooting

> **Study time:** ~35 minutes
> **Prerequisites:** [`12_LOCAL_SETUP.md`](./12_LOCAL_SETUP.md)

## Why this matters

"Debug / troubleshoot / work independently" is listed in the Smart Apartment Data JD alongside the technical skills — it is a soft skill with hard evidence. The deployment post-mortem in this project (four distinct bugs, all found and fixed through systematic investigation) is the most concrete story you have. Being able to tell that story step-by-step, name what tools you used to find each bug, and explain what you learned from it is worth more than any resume bullet.

By the end of this doc you will be able to: (1) attach a debugger to any of the four services in both Visual Studio and VS Code; (2) use the Hangfire dashboard and Serilog structured logs to diagnose failed jobs; (3) walk through all four Azure deployment bugs from symptom to root cause to fix.

---

## Debugging Mindset

Debugging is not guessing until something works. It is:

1. **Reproduce** — can you reliably cause the problem? If not, you are debugging a ghost.
2. **Isolate** — narrow the failing scope. Is it the SQL query? The EF mapping? The HTTP middleware? Remove layers until you find the boundary.
3. **Hypothesize** — form a specific, testable prediction. "I think the connection string is wrong because the env var is shadowed by appsettings.json."
4. **Verify** — test the hypothesis with the smallest possible experiment (a log line, a breakpoint, a curl command). Do not write 200 lines of code to test a hypothesis you could verify in 5 minutes.

The mistake that causes most debugging sessions to take hours instead of minutes: jumping to step 4 (writing code) before completing step 3 (forming a hypothesis).

---

## Visual Studio 2022 — Debugger

### Attach to a running process

1. Start the service: `dotnet run --project src/MDH.AnalyticsApi`
2. In VS: **Debug → Attach to Process** (`Ctrl+Alt+P`)
3. Find `dotnet.exe` / `MDH.AnalyticsApi.exe` in the list
4. Select and click Attach

**Breakpoints:** Click in the left gutter or press F9. Breakpoints survive recompilation without reattaching.

**Conditional breakpoints:** Right-click a breakpoint → Conditions → "Expression: `request.Page > 10`". Pauses only when the condition is true. Useful for catching rare paths without stopping on every hit.

**Logpoints:** Right-click a breakpoint → Actions → "Log a message to Output Window: `Listing count: {listings.Count}`". Emits a message without pausing execution. Use this in production-like timing tests where pausing would skew results.

**Watch window:** Add any expression. During a paused session, type `listings.Count` or `db.Database.GetConnectionString()` to inspect live state.

**Immediate window:** Execute arbitrary code: `_db.DimListings.Count()`, `DateTime.UtcNow.ToString("O")`. Full C# expression evaluator.

**Parallel Stacks (Debug → Windows → Parallel Stacks):** Shows all threads and their call stacks simultaneously. Essential for diagnosing deadlocks or threading issues in `BackgroundService` workers.

**Async call stacks:** In .NET 9, the debugger correctly unwinds async continuations. If you pause inside `GetMarketsQueryHandler.Handle()`, the call stack shows the `MediatR` dispatcher and the Minimal API route above it — even through `await` boundaries.

---

## VS Code — Debugger

Add `.vscode/launch.json` (already documented in `12_LOCAL_SETUP.md`):

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Attach to AnalyticsApi",
      "type": "coreclr",
      "request": "attach",
      "processName": "MDH.AnalyticsApi"
    },
    {
      "name": "Launch OrchestrationService",
      "type": "coreclr",
      "request": "launch",
      "program": "${workspaceFolder}/src/MDH.OrchestrationService/bin/Debug/net9.0/MDH.OrchestrationService.dll",
      "cwd": "${workspaceFolder}/src/MDH.OrchestrationService",
      "env": { "ASPNETCORE_ENVIRONMENT": "Development" }
    }
  ]
}
```

For **multi-process debugging** (all 4 services simultaneously), add one configuration per service and use the VS Code "compound" launch type:

```json
{
  "compounds": [{
    "name": "All Services",
    "configurations": ["Launch AnalyticsApi", "Launch OrchestrationService", ...]
  }]
}
```

---

## 🔴 Breakpoint Map

| File | Line | Comment | What to inspect |
|---|---|---|---|
| `src/MDH.IngestionService/Persistence/RawListingStore.cs` | ~33 | `🔴 BREAKPOINT: raw batch persisted` | `listings.Count` (expect 1000 local), `listings[0]` shape |
| `src/MDH.OrchestrationService/Jobs/CleanListingsJob.cs` | ~23 | `🔴 BREAKPOINT: starting ETL batch` | `raw.Count`, `submarketMap` contents |
| `src/MDH.OrchestrationService/Jobs/BuildMarketMetricsJob.cs` | ~20 | `🔴 BREAKPOINT: starting ETL batch` | `rentData.Count`, group keys |
| `src/MDH.OrchestrationService/Jobs/DetectAnomaliesJob.cs` | ~18 | `🔴 BREAKPOINT: starting ETL batch` | `mean`, `stdDev`, `zScore` per group |
| `src/MDH.AnalyticsApi/Features/Markets/GetMarketsQuery.cs` | ~23 | `🔴 BREAKPOINT: query received` | `submarkets`, `latestDate`, `latestMetrics` |
| `src/MDH.AnalyticsApi/Features/Listings/SearchListingsQuery.cs` | ~20 | `🔴 BREAKPOINT: query received` | `request` object, generated SQL |
| `src/MDH.InsightsService/Anthropic/AnthropicClient.cs` | ~18 | `🔴 BREAKPOINT: prompt assembled, sending to Claude` | `systemPrompt` (full text), `userMessage` (injected JSON) |

---

## Serilog Structured Logs

Serilog outputs JSON-structured logs. In the console output, a structured message looks like:

```
[14:23:11 INF] CleanListingsJob starting ETL batch
[14:23:11 INF] Processing {Count} raw listings Count=4756
[14:23:12 INF] CleanListingsJob complete: processed {Count} listings Count=4750
```

**Filtering by property in Log Analytics (Azure):**

```kusto
ContainerAppConsoleLogs_CL
| where ContainerName_s == "ca-mdh-orchestration"
| where RawData contains "CleanListingsJob"
| order by TimeGenerated desc
| take 50
```

**Filtering by CorrelationId:**

```kusto
ContainerAppConsoleLogs_CL
| where RawData contains "a1b2c3d4-e5f6-7890"
| order by TimeGenerated asc
```

Structured logging is what makes this possible. If the correlation ID were embedded in a free-text string (not a named parameter), you would need a regex. Named parameters in Serilog are always key-value pairs in the underlying JSON, queryable directly.

---

## Hangfire Dashboard Forensics

Open `http://localhost:5020/hangfire`.

**Jobs → Succeeded:** Every successful execution with duration. If `CleanListingsJob` shows "0ms" duration, it processed no documents (all already processed) — not a failure, just an empty tick.

**Jobs → Failed:** Stack trace, exception type, job arguments. The retry counter shows how many times Hangfire retried before marking it failed.

**Jobs → Processing:** Jobs currently running. If a job is stuck in "Processing" after the expected duration, the worker process may have crashed mid-execution. Hangfire's "invisibility timeout" will eventually requeue stuck jobs.

**Recurring Jobs:** Shows the three jobs with next execution time, last execution time, last status. If a recurring job disappears from this page, check `Program.cs` — the `RecurringJob.AddOrUpdate` call may have been removed.

**State transitions:** Each job moves through states: Enqueued → Processing → Succeeded / Failed. The state history is stored in SQL Server. You can query it directly:

```sql
SELECT * FROM hangfire.Job WHERE StateName = 'Failed' ORDER BY CreatedAt DESC;
```

---

## SQL Server Diagnostics

**Execution plans in SSMS:** Press `Ctrl+M` before running a query. The plan shows whether EF Core-generated SQL uses index seeks or scans. A scan on `fact_daily_rent` for a single-listing lookup = missing index.

**Missing index DMVs:**
```sql
SELECT TOP 10
    migs.avg_total_user_cost * migs.avg_user_impact * (migs.user_seeks + migs.user_scans) AS improvement,
    mid.statement AS table_name,
    mid.equality_columns, mid.inequality_columns, mid.included_columns
FROM sys.dm_db_missing_index_groups mig
JOIN sys.dm_db_missing_index_group_stats migs ON mig.index_group_handle = migs.group_handle
JOIN sys.dm_db_missing_index_details mid ON mig.index_handle = mid.index_handle
ORDER BY improvement DESC;
```

**Query Store** (SQL Server 2022): `sys.query_store_runtime_stats` shows historical query performance. Useful for detecting query plan regression after a schema change or statistics update.

---

## MongoDB Diagnostics

**`currentOp()`** — shows running operations:
```javascript
db.adminCommand({ currentOp: true, active: true })
```

**`explain("executionStats")`** — shows how a query is executed:
```javascript
db.listings_raw.find({ processed: false }).explain("executionStats")
// Look for: executionStats.totalDocsExamined vs nReturned
// If examined >> returned, an index would help
```

**Profiler:** `db.setProfilingLevel(2)` logs all operations to `system.profile`. Use sparingly — significant overhead.

---

## HTTP Debugging

**`curl -v`** — shows full request and response including headers:
```bash
curl -v -H "Authorization: Bearer <token>" http://localhost:5030/api/v1/markets
```

**HttpClient logging handler** — add request/response logging in .NET:
```csharp
builder.Logging.AddFilter("System.Net.Http", LogLevel.Trace);
```
Logs every HTTP request made by `HttpClient`, including headers and status codes. Very verbose but invaluable for debugging `InsightsService → AnalyticsApi` calls.

**Postman / Insomnia:** Import the OpenAPI spec from `/swagger/v1/swagger.json` to automatically generate a collection with all endpoints pre-configured.

---

## .NET Runtime Diagnostics

**`dotnet-counters`:** Real-time CPU, GC, threadpool metrics:
```bash
dotnet-counters monitor --name MDH.AnalyticsApi
```

**`dotnet-dump`:** Capture a process memory dump:
```bash
dotnet-dump collect --process-id <pid>
dotnet-dump analyze <dump_file>
```

**`dotnet-trace`:** CPU sampling and event tracing:
```bash
dotnet-trace collect --name MDH.OrchestrationService --duration 00:00:30
```

---

## Case Study — The 4-Bug Azure Post-Mortem

These four bugs were found and fixed during the Azure deployment. Each illustrates a different debugging discipline.

### Bug 1 — `PendingModelChangesWarning` thrown as exception

**Symptom:** OrchestrationService started, logged `[FTL] System.InvalidOperationException: The model for context 'WarehouseDbContext' has pending changes`, and exited.

**Initial hypothesis:** A migration was missing or unapplied.

**Investigation:** Ran `dotnet ef migrations list` — reported the migration as existing. Ran `dotnet ef database update` — reported "Done." but tables were not created. Compared `WarehouseDbContextModelSnapshot.cs` against `OnModelCreating` in `WarehouseDbContext.cs`.

**Root cause:** The snapshot file lacked `HasData(...)` for the 12 submarket seed rows that `OnModelCreating` declared. EF Core 9 promotes this mismatch (model hash mismatch) from a warning to an error.

**Fix:** Added `HasData(...)` to the snapshot + `ConfigureWarnings(w => w.Log(PendingModelChangesWarning))` to downgrade from error to log.

**Lesson:** EF Core 9 is stricter than EF Core 8. Hand-written migrations must keep the snapshot in sync with `OnModelCreating`.

### Bug 2 — Missing `[Migration]` attribute on hand-written migration class

**Symptom:** `dotnet ef migrations list` reported "No migrations were found." `MigrateAsync()` logged "0 pending migrations" and returned — no tables were created.

**Investigation:** Examined `InitialWarehouseSchema.cs` against a known auto-generated migration. Found the auto-generated class had `[Migration("20240101000000_InitialWarehouseSchema")]` — the hand-written one did not.

**Root cause:** EF Core discovers migrations via reflection using the `[Migration]` attribute. Without it, `IMigrationsAssembly.FindMigrationsInAssembly()` does not see the class.

**Fix:** Added `[Migration("20240101000000_InitialWarehouseSchema")]` to the class declaration. Applied schema manually via sqlcmd as an immediate unblock.

**Lesson:** Auto-generated migrations include the attribute automatically. Hand-written migrations require it explicitly.

### Bug 3 — Configuration precedence (`appsettings.json` masking the cloud secret)

**Symptom:** Container Apps connected to `localhost:1433` instead of Azure SQL. Connection refused. ETL jobs immediately failed.

**Investigation:** Checked Container App env vars in Azure portal — `SQL_CONNECTION_STRING` was set correctly. Checked logs — the connection string logged was `Server=localhost,1433;Database=MDH;...`. Added a diagnostic log to print which connection string was being used. Found it was coming from `appsettings.json`.

**Root cause:** The code read:
```csharp
var sqlConn = builder.Configuration.GetConnectionString("SqlServer")
    ?? builder.Configuration["SQL_CONNECTION_STRING"]
    ?? throw new InvalidOperationException(...);
```
`GetConnectionString("SqlServer")` found the non-null localhost value in `appsettings.json` FIRST. The `??` (null-coalescing) fallback to the env var was never reached.

**Fix:** Reordered to check the explicit env var first:
```csharp
var sqlConn = builder.Configuration["SQL_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("SqlServer")
    ?? throw new ...;
```

**Lesson:** Configuration precedence in .NET is: env vars > appsettings.Environment.json > appsettings.json > in-memory defaults. But `GetConnectionString()` reads from ALL sources and returns the first non-null. If appsettings.json has a non-null value, it wins over an env var check made with `??` afterwards. Always check the env var explicitly first in the `??` chain when cloud overrides are needed.

### Bug 4 — `/health` conflating liveness with readiness

**Symptom:** Container Apps reported analytics-api as healthy, then Azure SQL auto-paused after 60 minutes, `/health` returned 503, Container Apps killed the container, restart loop.

**Investigation:** Read Container App revision logs — "Health check failed: GET /health returned 503". Checked `/health` implementation — it was running ALL health checks including the SQL Server check. Azure SQL serverless auto-pauses after 60 minutes of inactivity. The SQL probe timed out. 503 → Container Apps kill + restart → SQL wakes up → health check passes → repeats.

**Root cause:** Liveness and readiness were not separated. `/health` had `Predicate = _ => true` (run all checks including SQL). For a service that must stay alive even when its database is paused, liveness must not depend on the database.

**Fix:**
```csharp
app.MapHealthChecks("/health", new HealthCheckOptions {
    Predicate = _ => false  // Liveness: always 200 if process is alive
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("ready")  // Readiness: SQL check
});
```

**Lesson:** Liveness checks process health only. Readiness checks dependency health. Mixing them on a `/health` endpoint that is used as the liveness probe kills services during expected infrastructure pauses.

---

## Common Failure Recipes

| Symptom | Likely cause | First step to verify |
|---|---|---|
| `SqlException: connection refused` | Wrong connection string (localhost vs Azure) | Log the exact connection string at startup |
| `401 Unauthorized` on all API requests | JWT secret mismatch between generator and validator | Compare `JWT_SECRET` env var to what `JwtTokenGenerator` used |
| `PendingModelChangesWarning` | Snapshot out of sync with `OnModelCreating` | Diff `WarehouseDbContextModelSnapshot.cs` against entity classes |
| Hangfire job stuck in "Processing" | Worker crashed mid-job, invisibility timeout not elapsed | Check process logs for crash at job start time |
| MongoDB insert timeout | `MongoClient` created as transient, connection pool exhausted | Verify singleton registration in DI |
| CleanListingsJob processes 0 documents | All documents already processed, or `Processed = true` filter misconfigured | Check `db.listings_raw.countDocuments({processed: false})` |
| Azure SQL /health/ready returns 503 | SQL auto-paused after 60 min idle | Wait 60s for cold start, or hit `/health/ready` to wake it |

---

## Exercise

1. Set a conditional breakpoint in `DetectAnomaliesJob.cs` at the z-score check that fires only when `Math.Abs(zScore) > 3.0`. How many anomalies are flagged per run with the default synthetic data distribution?

2. Open Hangfire dashboard. Find a recent `CleanListingsJob` execution and check its duration. If it is under 100ms, why? (Hint: count unprocessed documents first.)

3. Reproduce Bug 3 locally: add `"ConnectionStrings": { "SqlServer": "Server=localhost,1433;..." }` to `appsettings.json` and run OrchestrationService without `SQL_CONNECTION_STRING` env var. What connection string is used?

4. Run `dotnet-counters monitor --name MDH.AnalyticsApi` while making 10 concurrent requests to `/api/v1/markets`. What happens to `threadpool-queue-length` and `gc-heap-size`?

---

## Common mistakes

- **Logging connection strings in production.** Adding `_logger.LogInformation("Using: {Conn}", sqlConn)` to debug Bug 3 would work locally, but leaks credentials in production logs. Log the server/database name only, never the password: `_logger.LogInformation("Connecting to {Server}", builder.DataSource)`.

- **Not including context in log messages.** `_logger.LogError(ex, "Failed")` is useless in production. Always include what you were doing: `_logger.LogError(ex, "Failed to process listing {ExternalId}", rawListing.ExternalId)`.

- **Using `Debug.Assert` in production.** `Debug.Assert` is compiled away in Release builds. Use `Guard.Against` or explicit `throw new InvalidOperationException(...)` for invariants that must hold in production.

- **Attaching a debugger to a production process.** Pausing execution in a production container freezes all requests to that instance. Use production-safe tools: `dotnet-trace` (non-pausing sampling), `dotnet-counters`, and structured log analysis.

- **Re-running the job manually after a Hangfire failure without fixing the root cause.** Hangfire's "Requeue" button re-runs the exact same job with the exact same arguments. If the root cause is still present, it will fail again. Fix first, requeue second.

---

## Interview Angle — Smart Apartment Data

1. **"Tell me about a difficult bug you debugged."** — Tell the Bug 3 story: the Azure connection string masking. Symptom: containers connected to localhost. Expected root cause: missing env var. Actual root cause: .NET configuration precedence — `GetConnectionString("SqlServer")` returned a non-null value from appsettings.json before the `??` chain reached the env var. Found via: adding a startup log of the exact connection string being used. Fixed by reordering the `??` chain to check the env var explicitly first.

2. **"How do you debug EF Core performance issues?"** — Enable `LogTo(Console.WriteLine, LogLevel.Information)` in `AddDbContext` options to log all generated SQL. Check execution plans in SSMS for scan vs seek. Use `AsNoTracking()` on read paths. Add NCIs for frequently filtered columns.

3. **"How do you troubleshoot a Hangfire job that is stuck in Processing?"** — Check process logs at the time the job started processing. If the process crashed, Hangfire's invisibility timeout (default 30 minutes) will requeue the job. If the job is genuinely running, check thread starvation via `dotnet-counters` (threadpool-queue-length > 0 for extended periods).

4. **"What structured logging library do you use and why?"** — Serilog. Named parameters become queryable key-value pairs in Log Analytics. You can filter `where Properties.CorrelationId == '...'` to trace a single ingest tick across all services. Without structured logging you would regex-parse free-text.

5. **30-second talking point:** "The debugging approach I use is: reproduce, isolate, hypothesize, verify — in that order. The Azure deployment had four bugs that I traced through container logs, az containerapp logs show, SSMS query output, and Git blame on the configuration file. Each one had a different root cause: snapshot drift, missing attribute, config precedence, and liveness/readiness conflation. The post-mortem is documented in AUDIT_REPORT.md. The most instructive was the config precedence bug — it taught me that you cannot rely on .env overriding appsettings.json unless you check the env var first in the ?? chain."

6. **Job requirement proof:** "Debug / troubleshoot / work independently" — four-bug post-mortem with documented symptom, investigation, root cause, fix, and lesson for each.
