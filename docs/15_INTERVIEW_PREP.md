# 15 — Interview Preparation

> **Study time:** ~45 minutes
> **Prerequisites:** Docs 00–14b (everything)

## Why this matters

All the technical knowledge in docs 00–14b is worthless if you cannot surface it clearly under interview pressure. This doc is your scripted preparation: a ready-to-speak elevator pitch, answers to the 25 most likely technical questions backed by this codebase, a system design story, salary anchor, and the questions you will ask them.

---

## 90-Second Elevator Pitch

> Speak this aloud until it takes under 90 seconds.

"I'm a backend C# developer with hands-on experience building data-intensive microservices. For this interview I built MultifamilyDataHub from scratch — a four-service platform that ingests synthetic rental listings, runs Hangfire ETL jobs to populate a SQL Server star schema, serves a CQRS REST API with JWT auth, and calls the Anthropic Claude API for AI-powered market summaries.

The project is deployed live on Azure Container Apps with free-tier SQL and Cosmos DB. I hit and solved five cloud-specific bugs during deployment — the most instructive was a .NET configuration precedence issue where appsettings.json was silently shadowing an Azure Container App secret. I diagnosed it through container logs, found the root cause in how `GetConnectionString()` resolves the `??` chain, and fixed it by reordering the config lookup.

I'm applying because Smart Apartment Data's core business is exactly the domain I worked in — multifamily rental data, submarket analytics, ETL pipelines, and data warehousing. I want to work on real data that operators use to make pricing decisions, not synthetic benchmarks."

---

## Why Smart Apartment Data — 3 Concrete Reasons

1. **Domain match.** The entire MultifamilyDataHub codebase mirrors SAD's problem domain: ingesting listing data, computing submarket metrics, detecting pricing anomalies. I understand asking rent vs effective rent vs concessions, the difference between a dim and a fact table, and why the grain of your fact table matters. I will not be learning the domain while learning the codebase.

2. **Data + backend intersection.** The JD asks for SQL Server, NoSQL, REST APIs, and big data orchestration in one role. That intersection — not a pure web API role, not a pure data engineering role — is exactly where I work best. The project demonstrates all four.

3. **Scale.** "Large datasets" is in the JD. `fact_daily_rent` scales to 10M+ rows/day locally and was designed with the same patterns (star schema, covering indexes, AsNoTracking, batch ETL) that hold at production scale.

---

## Tell Me About Yourself (2-Minute STAR)

**Situation:** I have been building backend C# systems for [N years], primarily focused on data-adjacent APIs and ETL pipelines.

**Task:** For this role, I wanted to demonstrate end-to-end fluency — from data ingest through warehouse modeling to API serving to AI integration.

**Action:** I built MultifamilyDataHub: a four-service microservices platform deployed on Azure Container Apps. MongoDB landing zone for raw ingest, SQL Server star schema for the curated warehouse, ASP.NET Core REST API with CQRS/MediatR, and an Anthropic Claude integration for market summaries. I hit five deployment bugs — region capacity, config precedence, liveness/readiness conflation, and more — and documented all of them in a post-mortem.

**Result:** Live deployment at approximately $0/month on Azure free tier. 23 passing unit tests across three test projects. Full Bicep IaC, GitHub Actions CI, and this 18-doc study curriculum I wrote for my own interview prep.

---

## Technical Q&A — 25+ Questions

### C# / .NET Fundamentals (6)

**Q1: What is the difference between `record`, `struct`, and `class` in C# 9+?**
`class` — reference type, heap allocated, reference equality by default. `struct` — value type, stack allocated for small payloads, value equality (compares field values). `record class` — reference type with generated value equality (`Equals`, `GetHashCode`) and `ToString`. `record struct` — value type with value equality. In MDH: `Money` and `BedBath` are `readonly record struct` — stack allocated, value equality, immutable. `ListingDto` is a `record` (class) — value equality for easy DTO comparison in tests.

**Q2: What is `Nullable<T>` / nullable reference types?**
Nullable VALUE types (`int?`, `DateTime?`) have existed since C# 2. Nullable REFERENCE types (NRT, `string?`, `List<T>?`) are a C# 8+ compiler feature enabled by `<Nullable>enable</Nullable>`. They teach the compiler to track nullability through the type system and warn at assignment and dereference sites. NRT does not change runtime behavior — it is pure compile-time analysis. In MDH, `<Nullable>enable</Nullable>` in `Directory.Build.props` applies it to all projects.

**Q3: What is a `BackgroundService` and how does graceful shutdown work?**
`BackgroundService` is the base class for .NET hosted services. `ExecuteAsync(CancellationToken)` runs indefinitely. The host calls `StopAsync()` on SIGTERM, which cancels the token. The `while (!stoppingToken.IsCancellationRequested)` loop exits at the next check. `Task.Delay(interval, stoppingToken)` unblocks immediately on cancellation. Every async call receives the token so they abort promptly rather than waiting out their full operation.

**Q4: What is the difference between `IOptions<T>`, `IOptionsMonitor<T>`, and `IOptionsSnapshot<T>`?**
`IOptions<T>` — singleton, reads config once at startup. `IOptionsSnapshot<T>` — scoped, reads config per request (recalculated on DI scope boundary). `IOptionsMonitor<T>` — singleton with change callbacks, supports hot-reload. For IngestionService's batch size (static after deploy), `IOptions<T>` or `GetValue<T>()` is correct. For a feature flag that changes during runtime, `IOptionsMonitor<T>`.

**Q5: What is the difference between `Task.Run`, `Task.Factory.StartNew`, and `await`?**
`await` suspends the current method and returns to the caller until the awaited task completes — no thread is blocked. `Task.Run` queues a delegate to the threadpool and returns a Task. Appropriate for CPU-bound work. `Task.Factory.StartNew` is `Task.Run`'s older, lower-level cousin with more options. In `BackgroundService.ExecuteAsync`, you `await` all async operations — no `Task.Run` needed because the execution loop itself IS the background thread.

**Q6: What is `IAsyncEnumerable<T>` and when would you use it?**
`IAsyncEnumerable<T>` allows yielding items from an async source one at a time with `await foreach`. Use it when: streaming large result sets from a database without loading all rows into memory (EF Core supports `AsAsyncEnumerable()`), or streaming from a network source. In MDH, `SearchListingsQuery` loads all matching listings into memory — at 100k+ rows this would be replaced with `AsAsyncEnumerable()` and cursor pagination.

---

### REST API Design (4)

**Q7: What HTTP status code do you return for a not-found resource vs a missing required field?**
Not-found resource: `404 Not Found`. Missing required field (validation failure): `400 Bad Request` or `422 Unprocessable Entity` (422 is more precise — the request was syntactically valid but semantically wrong). In MDH: `GetListingByIdQuery.cs` returns `Results.NotFound()` when the listing is absent.

**Q8: When would you use `PUT` vs `PATCH`?**
`PUT` replaces the entire resource — send all fields. `PATCH` updates specific fields — send only changed fields (JSON Merge Patch RFC 7386 or JSON Patch RFC 6902). `PUT` is idempotent; `PATCH` may or may not be depending on implementation. For a "rename submarket" operation, `PATCH /api/v1/markets/{id}` with `{ name: "Austin-North" }` is correct — you are not sending all submarket fields.

**Q9: How do you version a REST API?**
URI versioning: `/api/v1/` and `/api/v2/`. Simple, cache-friendly, widely understood. Header versioning: `Accept-Version: v2`. Clean URIs but not cache-friendly. MDH uses URI versioning. A breaking change — removing a field or changing a type — requires a new version. Adding optional response fields is backwards-compatible and does not require a version bump.

**Q10: What is idempotency and which HTTP methods are idempotent?**
An operation is idempotent if calling it multiple times produces the same result as calling it once. GET, PUT, DELETE, HEAD, OPTIONS are idempotent. POST is not (each call may create a new resource). Idempotency is critical for retry logic — if a PUT request times out, retrying it is safe. If a POST times out, retrying may create a duplicate. For non-idempotent operations that must be retried safely, use an `Idempotency-Key` header.

---

### SQL / T-SQL / Query Optimization (4)

**Q11: What is the difference between a clustered index and a non-clustered index?**
A clustered index IS the table — the rows are physically ordered by the index key. There can be only one per table. SQL Server creates it on the primary key by default. A non-clustered index is a separate B-tree structure that stores the index key plus a pointer (row locator) to the actual row. A table can have many NCIs. When a query uses an NCI and needs columns not in the index, SQL Server does a "key lookup" back to the clustered index — if key lookups are expensive, add `INCLUDE` columns to the NCI to make it covering.

**Q12: Explain the `N+1` query problem and how EF Core causes it.**
N+1 occurs when loading a collection triggers one query for the parent and N queries for each child. In EF Core: loading `_db.DimListings.ToListAsync()` and then accessing `listing.DailyRents` in a loop triggers one SQL query per listing (lazy loading) instead of one JOIN. Fix: `.Include(l => l.DailyRents)` for eager loading, or a separate `.Where(r => ids.Contains(r.ListingId)).ToListAsync()` and join in memory. MDH's `SearchListingsQuery.cs` uses `.Include(l => l.DailyRents)` — appropriate for small rent histories.

**Q13: What is a covering index?**
A covering index satisfies all columns needed by a query without a key lookup to the clustered index. If a query selects `ListingId, AskingRent` and the NCI index key is `(SubmarketId, RentDate)` but includes `INCLUDE (ListingId, AskingRent)`, the query is satisfied entirely from the NCI — no heap read. In MDH, `IX_fact_daily_rent_ListingId_RentDate` covers the idempotency check `AnyAsync(f => f.ListingId == id && f.RentDate == date)` because both columns are in the index key.

**Q14: When would you use a CTE vs a subquery vs a temp table?**
CTE (`WITH cte AS (...)`) — readable, reusable within the query, no materialization guarantee. Subquery — inline, can be correlated. Temp table (`#temp`) — materialized, can have indexes, persists for the session duration. Use CTEs for readability and recursive queries. Use temp tables when the intermediate result set is used multiple times (avoids recomputing) or is large enough to benefit from an index. For the BuildMarketMetricsJob aggregation query, a CTE organizing the rent data before grouping would be idiomatic.

---

### NoSQL / MongoDB (3)

**Q15: What is a TTL index in MongoDB and how does it work?**
A TTL (Time To Live) index is a single-field index on a Date field with `expireAfterSeconds`. MongoDB's background task periodically scans the index and deletes documents whose date is older than the threshold. Used for automatic cleanup of ephemeral data (sessions, logs, raw landing zone documents). In MDH production, a TTL on `scraped_at` (30 days) would prevent `listings_raw` from growing unboundedly.

**Q16: When is MongoDB the wrong choice?**
When you need: multi-document ACID transactions across collections (supported but expensive and limited), complex multi-join queries (`$lookup` beyond two collections is unwieldy), strict referential integrity (no FK constraints), or when your team has no MongoDB experience and the domain is fully relational. Rule of thumb: if you find yourself wanting `$lookup` on more than two collections, the data is relational and belongs in SQL.

**Q17: How does write concern `w: majority` differ from `w: 1`?**
`w: 1` acknowledges after the primary records the write. Fast, but the write can be lost if the primary crashes before replication. `w: majority` waits for a majority of replica set members to confirm the write before acknowledging. Durable but ~2–5ms slower per write. For a landing zone with re-generatable synthetic data, `w: 1` is the correct tradeoff. For an order or payment record, `w: majority`.

---

### Big Data Orchestration / Data Pipelines (4)

**Q18: What is Hangfire and what are its limitations?**
Hangfire is an in-process .NET scheduler with SQL Server persistent storage. It supports fire-and-forget, delayed, recurring, and continuation jobs. Dashboard at `/hangfire`. Limitations: no DAG (complex job dependency graphs require workarounds), no cross-language support, single-process bottleneck for CPU-heavy jobs, SQL Server storage limits horizontal worker scaling to soft limits. Right choice: small-medium .NET workloads with a single ops target.

**Q19: What is the difference between ETL and ELT?**
ETL: Extract → Transform → Load. Data is transformed before landing in the destination. Old-school pattern, requires knowing the target schema before loading. ELT: Extract → Load → Transform. Raw data lands first; transformations run in the destination. Enabled by cheap cloud storage and columnar databases. MDH uses a medallion variant: raw data in MongoDB (Bronze), cleaned in SQL Server (Silver), aggregated in place (Gold).

**Q20: Explain the 3-sigma anomaly detection math in DetectAnomaliesJob.**
For each submarket × bedroom band: compute the mean `μ` and population standard deviation `σ` of asking rents. Z-score = `(rent - μ) / σ`. Any listing with `|z| ≥ 3.0` is flagged. Under a normal distribution, |z| ≥ 3.0 occurs with ~0.3% probability. Limitations: assumes normality (rent is right-skewed), masking effect (cluster of outliers inflates σ), only works for groups of ≥ 5 listings. Production improvement: IQR-based flagging or Isolation Forest.

**Q21: How would you scale the ETL pipeline to 10M listings/day?**
Replace MongoDB polling with Azure Service Bus / Kafka (partitioned by submarket). Replace `CleanListingsJob` singleton with N parallel workers consuming from the queue. Replace in-memory LINQ aggregation in `BuildMarketMetricsJob` with T-SQL `GROUP BY` pushed to the database. Add a columnstore index on `fact_daily_rent` for analytics queries. Partition `fact_daily_rent` by month for archival and query pruning. Consider Databricks Delta Lake for the multi-billion-row tier.

---

### Microservices / Cloud / Azure (4)

**Q22: When is a monolith better than microservices?**
Monolith wins when: single team, unstable domain (bounded contexts shifting), operational complexity would dominate development, services must be deployed together anyway, CRUD app with no SLA differences between components. "Monolith first, extract services when you feel the pain" is almost always the right default. MDH uses microservices because the goal is demonstrating distributed systems fluency and because there are genuine SLA isolation reasons (AI calls must not block warehouse reads).

**Q23: Explain the liveness vs readiness probe difference.**
Liveness: is the container alive? Failure → restart. Must not check external dependencies. Readiness: is the container ready for traffic? Failure → remove from load balancer, do NOT restart. Can check SQL, cache warmup. Mixing them on a service with an auto-pausing database causes a restart loop — the database pauses, readiness fails, liveness triggers a restart, the container restarts, SQL is still waking up, loop repeats.

**Q24: How does managed identity work in Azure?**
A Container App (or VM, Function) can have a system-assigned managed identity — an entry in Azure Active Directory automatically managed by Azure. The application calls Azure AD from within the Azure network to get an access token, then uses that token to call Azure services (Key Vault, SQL, Cosmos). No stored credentials anywhere. The "secret" is the Azure-attested identity of the resource itself. MDH currently uses connection string secrets — managed identity is the production upgrade.

**Q25: What is Azure Container Apps' consumption plan and how does billing work?**
The consumption plan charges for actual compute consumed: vCPU-seconds × rate + memory GiB-seconds × rate. The free grant (180k vCPU-s + 360k GiB-s + 2M requests/month) covers demo traffic entirely. Scale-to-zero means 0 replicas = $0 compute. The database charges are separate (SQL: 100k vCore-s free; Cosmos: free tier 1000 RU/s permanent).

---

### DevOps / Docker / CI-CD (2)

**Q26: Explain multi-stage Docker builds.**
Multi-stage builds use multiple `FROM` instructions in one Dockerfile. The first stage (SDK) compiles the application. The final stage (runtime) copies only the compiled output. The result: a small runtime image (~200MB) that does not contain the build tools (~800MB). In MDH, the Dockerfile has a `build` stage using `dotnet/sdk:9.0` and a `final` stage using `dotnet/aspnet:9.0`. Source code is not present in the production image.

**Q27: What is a GitHub Actions workflow and how does it differ from GitLab CI?**
A GitHub Actions workflow is a YAML file in `.github/workflows/` that defines jobs triggered by events (push, PR, dispatch). Each job runs on a runner (ubuntu-latest, windows-latest) and executes steps (shell commands or pre-built actions from the marketplace). GitLab CI uses `.gitlab-ci.yml` with a similar structure but different syntax — `stages:` groups jobs, `image:` sets the container, scripts run as shell commands directly. The concepts (triggers, jobs, steps, artifacts) are identical; the syntax differs.

---

### AI Tools in Workflow (2)

**Q28: How does InsightsService use the Anthropic Claude API?**
HTTP POST to `https://api.anthropic.com/v1/messages` with a JSON body: `{ model: "claude-sonnet-4-20250514", max_tokens: 1024, system: <prompt from .md file>, messages: [{ role: "user", content: <market data JSON> }] }`. The response contains `content[0].text` — the generated market summary. Polly retry (3 attempts, exponential backoff) and circuit breaker (5 failures → 30s open) wrap every call.

**Q29: How do you use AI in your development workflow (not just runtime)?**
Claude Code (Anthropic's CLI tool) was used to generate the entire codebase: initial scaffolding, EF Core migrations, Bicep templates, Dockerfiles, unit tests, and this 18-doc curriculum. The AI integration is therefore double: runtime (InsightsService calls Claude for market summaries) and development-time (Claude Code wrote and debugged the codebase). When the Azure deployment had bugs, I used Claude Code to work through the logs and generate fixes — a concrete example of AI-assisted debugging.

---

### Debugging / Troubleshooting (2)

**Q30: Tell me about a difficult bug you found and fixed.**
> Use Bug 3 from `AUDIT_REPORT.md` — the configuration precedence bug.

"OrchestrationService was connecting to localhost:1433 instead of Azure SQL. I checked Container App env vars in the portal — `SQL_CONNECTION_STRING` was set correctly. But the connection string in the logs showed the localhost value. I added a diagnostic log to print the exact string being used and confirmed it came from appsettings.json. The root cause: `GetConnectionString("SqlServer")` reads from all config sources and returns the first non-null. appsettings.json had a non-null localhost default, so the `??` fallback to the env var was never reached. Fix: reorder to check the env var first. Lesson: .NET configuration precedence can silently mask cloud secrets."

**Q31: How do you approach a production outage?**
1. Check liveness — is the service up? Check readiness — can it reach its dependencies?
2. Check recent deployments — was anything changed in the last 2 hours?
3. Check logs — structured logs in Log Analytics, filter by `ContainerName` and time window.
4. Isolate — is it one service or all services? One endpoint or all endpoints?
5. Hypothesize + verify quickly — don't write code; use curl, sqlcmd, az CLI to test hypotheses.
6. Fix forward or roll back — if you know the fix, fix it. If you don't, roll back the last deployment.
7. Post-mortem — document symptom, root cause, fix, lesson within 24 hours.

---

## Code Review Exercise

Open `src/MDH.OrchestrationService/Jobs/CleanListingsJob.cs`.

**What's good:**
- `// 🔴 BREAKPOINT` comment for debuggability
- Deduplication logic is explicit and readable (GroupBy + OrderByDescending + First)
- Idempotency check before inserting fact row (AnyAsync before Add)
- Proper exception handling per-listing — one bad listing doesn't kill the batch
- MarkProcessedAsync in bulk at the end — efficient

**What's debatable:**
- `SaveChangesAsync()` inside the `foreach` loop — one SQL round trip per listing. For 5000 records, this is 5000+ SQL calls. Batch-save (collect all entities, then SaveChanges once) would be 10–100x faster.
- Loading the submarket map with `ToDictionaryAsync` on every job tick — correct but could be cached

**What I would refactor:**
Move `_db.SaveChangesAsync()` out of the loop. Collect all `DimListing` inserts and `FactDailyRent` inserts, then call `await _db.SaveChangesAsync()` once after the `foreach`. Handle bulk insert failures by logging and continuing, not by saving one at a time.

---

## System Design — Scale to 10M Listings/Day

"How would you scale MultifamilyDataHub to handle 10 million listings per day?"

**Ingestion tier:**
Replace IngestionService's MongoDB direct insert with a partitioned Azure Service Bus queue (12 partitions, one per submarket). Multiple scraper workers publish to the queue. IngestionService becomes a queue consumer — each consumer handles one partition.

**Landing zone:**
MongoDB `listings_raw` becomes an Azure Blob Storage Parquet landing zone or stays as Cosmos DB with autoscale (to handle RU bursts). TTL policy: 30 days.

**ETL tier:**
Replace Hangfire `CleanListingsJob` with Azure Data Factory or Databricks Structured Streaming job consuming from the queue. SQL Server MERGE for upserts instead of application-level EF Core. BULK INSERT for fact rows.

**Warehouse:**
Partition `fact_daily_rent` by month. Add columnstore index for aggregation queries. Read replicas for AnalyticsApi. `fact_market_metrics` computation moves to a SQL Server stored procedure scheduled daily.

**API tier:**
Add Redis output cache for `/markets` (5-minute TTL). Add read replicas. Add CDN for GET responses.

**Observability:**
Add OpenTelemetry traces across all services. Azure Monitor Application Insights for distributed traces and anomaly alerts.

---

## Questions I Will Ask the Interviewer

1. "What does the data pipeline look like today — how does raw listing data get into your warehouse, and what tools manage the ETL?"

2. "What is the largest table in your SQL schema by row count, and what are the query patterns that hit it hardest?"

3. "How do you handle schema evolution for the raw data — when an operator changes their API response format, what breaks and how do you recover?"

4. "What does the on-call rotation look like for backend engineers? What percentage of incidents are data pipeline issues vs API issues?"

5. "Is there room to introduce AI features on top of the data — for example, using Claude to generate natural-language market summaries or anomaly explanations?"

6. "What does the typical onboarding look like for a new backend engineer — how long before a new hire ships their first feature to production?"

7. "What part of the current stack do senior engineers wish they had done differently?"

8. "What are the most impactful engineering improvements planned for the next two quarters?"

---

## Red Flags and How to Probe Politely

Based on research into the company and common patterns at data startups:

**"We move fast and break things"** → Ask: "What does your incident retrospective process look like? How long does a typical P1 incident take to resolve?"

**"Wearing many hats"** → Clarify: "Is the backend role expected to own infrastructure/DevOps as well, or is there a platform or SRE function?"

**"Contractor-to-hire"** → Ask directly: "This is listed as full-time — is there a contractor period before conversion, and what determines the conversion timeline?"

**Vague technical stack** → Ask: "What is your primary language and framework for new services? Are there plans to migrate any legacy components?"

**No growth path mentioned** → Ask: "What does the path from mid-level to senior engineer look like on your team? What distinguishes the two?"

---

## Salary Anchor

Budget ceiling: $3,500/month ($42,000/year). Scripted response if asked about compensation expectations:

"Based on my research and the scope of this role, I'm targeting $3,500 per month as base compensation. I'm flexible on total package structure and open to discussing the full offer — I'm primarily focused on the technical scope and growth opportunity."

Do not reveal the ceiling first. If pressed: "I'd rather hear the range you have budgeted and go from there." If their range is below your floor: "That is below what I can consider — is there flexibility in the budget?"

---

## Day-1 / Week-1 / Month-1 Plan

**Day 1:** Set up local environment (clone, Docker, dotnet run). Read all tickets in the current sprint. Understand the data schema — what are the tables, what is the grain, what are the key queries.

**Week 1:** Pair with a senior engineer on a small bug fix or feature. Read the last three post-mortems. Map the data flow from raw ingest to API response end-to-end. Meet the data team.

**Month 1:** Own a full feature end-to-end — schema change, migration, API endpoint, tests, deploy. Contribute to at least one post-mortem. Identify one technical debt item and propose a fix.
