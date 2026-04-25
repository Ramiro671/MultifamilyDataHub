# 02 — Architecture

> **Study time:** ~25 minutes
> **Prerequisites:** [`01_PROJECT_STRUCTURE.md`](./01_PROJECT_STRUCTURE.md)

## Why this matters

Architecture decisions outlive code. When you join a team, you inherit their architectural bets — and you will be asked to defend, extend, or argue against them in design discussions. Understanding *why* this project uses four separate services instead of one, *why* data flows from MongoDB into SQL Server rather than straight to SQL, and *why* the AI layer is isolated behind a circuit breaker are the kinds of answers that distinguish engineers who read code from engineers who reason about systems.

Recruiters for data-adjacent backend roles often ask "walk me through your system" as the first technical question. A clean mental model of this architecture is your entry point to every technical topic in this curriculum.

By the end of this doc you will be able to: (1) draw the full data flow from Bogus generation through MongoDB, SQL Server, REST API, and Anthropic Claude; (2) articulate the real tradeoffs of microservices vs a monolith for this use case; (3) explain what the medallion (landing/curated zone) pattern is and why it exists.

---

## Elevator Pitch

MultifamilyDataHub is a production-grade backend microservices platform that ingests, curates, and analyzes synthetic multifamily rental listing data across 12 US submarkets. It demonstrates a complete data engineering pipeline — from raw MongoDB landing zone through SQL Server star-schema warehouse to AI-powered market insights via Claude — using C# 13 / .NET 9, EF Core 9, Hangfire, MediatR, and Polly.

The system is intentionally designed to mirror the core concerns of a real rental-data platform: high-volume raw ingest, ETL normalization, statistical anomaly detection, queryable analytics, and natural-language AI summarization. Every component is independently deployable, runs on Azure Container Apps (scale-to-zero free tier), and is wired for observability via Serilog structured logging.

---

## Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│                        MultifamilyDataHub                                │
│                                                                          │
│  ┌──────────────────────────┐     ┌───────────────────────────────────┐ │
│  │  MDH.IngestionService    │     │  MDH.OrchestrationService          │ │
│  │  (Worker, port 5010)     │     │  (Worker + Hangfire, port 5020)    │ │
│  │                          │     │                                    │ │
│  │  Bogus synthetic data    │────▶│  CleanListingsJob (1 min)          │ │
│  │  1000 listings/tick      │     │  BuildMarketMetricsJob (5 min)     │ │
│  │  10s interval            │     │  DetectAnomaliesJob (hourly)       │ │
│  │                          │     │                                    │ │
│  └────────────┬─────────────┘     └───────────────┬───────────────────┘ │
│               │                                   │                     │
│               ▼                                   ▼                     │
│  ┌────────────────────────┐     ┌──────────────────────────────────┐   │
│  │  MongoDB 7             │     │  SQL Server 2022                 │   │
│  │  mdh_raw.listings_raw  │     │  warehouse.dim_listing           │   │
│  │  (Landing Zone)        │     │  warehouse.dim_submarket         │   │
│  └────────────────────────┘     │  warehouse.fact_daily_rent       │   │
│                                  │  warehouse.fact_market_metrics   │   │
│                                  │  warehouse.fact_anomaly          │   │
│                                  │  hangfire.* (job storage)        │   │
│                                  └──────────────┬───────────────────┘   │
│                                                 │                       │
│                                  ┌──────────────▼───────────────────┐   │
│                                  │  MDH.AnalyticsApi (port 5030)    │   │
│                                  │  REST CQRS endpoints             │   │
│                                  │  JWT Bearer auth, Swagger        │   │
│                                  └──────────────┬───────────────────┘   │
│                                                 │                       │
│                                  ┌──────────────▼───────────────────┐   │
│                                  │  MDH.InsightsService (port 5040) │   │
│                                  │  POST /insights/market-summary   │   │
│                                  │  POST /insights/listing-explain  │   │
│                                  │  Polly retry + circuit breaker   │   │
│                                  └──────────────┬───────────────────┘   │
│                                                 │                       │
└─────────────────────────────────────────────────│───────────────────────┘
                                                  │
                                                  ▼
                                     ┌────────────────────────┐
                                     │  Anthropic Claude API   │
                                     │  claude-sonnet-4-...    │
                                     └────────────────────────┘
```

---

## End-to-End Data Flow

```
1. INGEST (src/MDH.IngestionService/IngestionWorker.cs)
   IngestionService wakes every 10 seconds
   → ListingFaker.GenerateBatch(1000, correlationId) produces synthetic BSON
   → RawListingStore.InsertManyAsync() writes to MongoDB mdh_raw.listings_raw
   → Each document: { external_id, submarket, bedrooms, asking_rent, ..., processed: false }

2. CLEAN (src/MDH.OrchestrationService/Jobs/CleanListingsJob.cs)
   Hangfire fires CleanListingsJob every minute
   → Reads up to 5000 unprocessed MongoDB documents
   → Deduplicates by external_id (last-write-wins)
   → Upserts warehouse.dim_listing (new listing or update LastUpdatedAt)
   → Inserts warehouse.fact_daily_rent (one row per listing per calendar day)
   → Marks MongoDB docs processed: true

3. AGGREGATE (src/MDH.OrchestrationService/Jobs/BuildMarketMetricsJob.cs)
   Hangfire fires BuildMarketMetricsJob every 5 minutes
   → Reads fact_daily_rent for today, joins to dim_listing
   → Groups by SubmarketId × Bedrooms
   → Computes avg/median effective rent, rent_per_sqft, occupancy estimate
   → Upserts warehouse.fact_market_metrics

4. ANOMALY DETECT (src/MDH.OrchestrationService/Jobs/DetectAnomaliesJob.cs)
   Hangfire fires DetectAnomaliesJob hourly
   → Reads today's fact_daily_rent joined to dim_listing
   → Per submarket/bedroom band: computes mean + population stddev
   → Any listing with |z-score| >= 3.0 → insert warehouse.fact_anomaly

5. QUERY (src/MDH.AnalyticsApi/)
   HTTP consumer calls e.g. GET /api/v1/markets
   → MediatR dispatches to GetMarketsQueryHandler
   → EF Core query with AsNoTracking() reads warehouse tables
   → Returns structured JSON

6. INSIGHT (src/MDH.InsightsService/)
   HTTP consumer calls POST /api/v1/insights/market-summary
   → InsightsService calls AnalyticsApi to fetch metrics
   → Assembles prompt from Prompts/market-summary.md template
   → Posts to Anthropic claude-sonnet-4-20250514
   → Returns natural-language summary + raw stats JSON
```

---

## Why Microservices — The Honest Tradeoffs

Microservices are not inherently better than a monolith. They are a tradeoff, and the tradeoff only pays off under specific conditions.

**Monolith is better when:**
- You have a single team and the domain is still shifting
- Operational complexity (container orchestration, distributed tracing) would outweigh delivery speed
- Each part of the system must be deployed together anyway
- You are building a CRUD app and the team is under 10 engineers

**This project earns its microservice complexity because:**

1. **SLA isolation** — IngestionService failing does not take down AnalyticsApi. OrchestrationService running a long ETL batch does not block REST queries. InsightsService being rate-limited by Anthropic does not affect warehouse reads.
2. **Independent scaling** — On Azure Container Apps, AnalyticsApi can scale to 5 replicas for query traffic while IngestionService runs as a single replica. Each scales on its own metrics.
3. **AI blast radius containment** — Anthropic API calls are slow (500ms–3s), expensive, and occasionally fail. Isolating them behind a circuit breaker means a Claude outage cannot cascade to warehouse queries.
4. **Portfolio demonstration** — Distributed systems design, Docker, Bicep, health check separation, and service-to-service JWT auth are explicit job requirements.

**Honest limitation:** For a real two-person startup, this would be overengineered. A single ASP.NET Core app with Hangfire embedded and a SQL + Mongo backend delivers the same features with 80% less operational surface. Microservices are justified here because the goal is demonstrating distributed systems fluency to a hiring committee.

---

## Landing Zone + Curated Zone — Medallion Pattern

This project implements a simplified two-tier medallion architecture.

**Bronze / Landing Zone (`MongoDB mdh_raw.listings_raw`):**
Data arrives exactly as produced — no schema enforcement, no validation, just raw BSON documents with a `processed: false` flag. In a real scraping pipeline, different operators have different schemas, some fields are missing, some are malformed strings. The landing zone accepts everything so no data is ever lost due to transformation bugs. You can always re-process the raw data by resetting the `processed` flag.

**Silver / Curated Zone (`SQL Server warehouse.*`):**
After `CleanListingsJob` runs, data is normalized (trimmed, deduped, type-converted), validated (unknown submarkets are logged and skipped), and structured into the star schema. Every column has a defined type and constraint. Consumers (AnalyticsApi) read only from here.

**Gold / Aggregated (`fact_market_metrics`, `fact_anomaly`):**
Pre-computed by `BuildMarketMetricsJob` and `DetectAnomaliesJob`. These tables are query-ready without further computation — the API just reads them.

The Databricks community popularized this as Bronze–Silver–Gold. Our version is simplified: no streaming layer, no Delta Lake, no Spark. It is the pattern applied at .NET scale.

---

## Architectural Decisions

| Decision | Choice | Rationale | When you'd choose differently |
|---|---|---|---|
| Microservices vs monolith | 4 services | SLA isolation, independent scaling, portfolio signal | Single team, unstable domain → start with monolith |
| Star schema | dim_* + fact_* | Industry standard for analytics; optimized for aggregation | If writes >> reads → normalized OLTP |
| Hangfire vs Quartz.NET | Hangfire | Built-in dashboard, simpler SQL Server storage | Cross-language pipelines or complex DAGs |
| MongoDB for landing zone | MongoDB | Schema-flexible raw ingest; blind inserts are fast | Schema fully known upfront → SQL directly |
| CQRS in AnalyticsApi | MediatR | Clean read-model separation; testable handlers | Small CRUD apps → overhead isn't justified |
| Polly in InsightsService | Circuit breaker + retry | Anthropic has 429s and 5xx bursts | Internal services on reliable LAN |
| JWT HS256 | Symmetric secret | Simplest viable auth for demo; single issuer | Multi-issuer → RS256 with public key distribution |
| Manual migration vs dotnet-ef | Hand-written migration | CI has no database; migration runs from code at startup | Production at scale → dedicated migration job |

---

## Non-Goals

What this system intentionally does NOT do:
- No real scraper — IngestionService uses Bogus synthetic data
- No auth server — JWT secret is shared directly, no OIDC
- No public frontend — all endpoints are REST/JSON only
- No event bus — Kafka/RabbitMQ would replace MongoDB polling in production
- No distributed tracing — Serilog structured logs only, no OpenTelemetry spans
- No VNet integration — all Azure services have public endpoints in the demo

Each omission has an upgrade path described in [`14a_AZURE_PRODUCTION_DEPLOYMENT.md`](./14a_AZURE_PRODUCTION_DEPLOYMENT.md).

---

## Exercise

1. Open `src/MDH.InsightsService/Program.cs` lines 91–93 and explain why the readiness check hits AnalyticsApi's `/health` rather than a database.

2. Trace the full path of a single listing from `ListingFaker.GenerateBatch()` through MongoDB into `fact_daily_rent`. Name every file and method touched in order.

3. The `CleanListingsJob` deduplicates by `external_id` with last-write-wins. What would you change in the data model to preserve every rent observation instead of overwriting?

4. If OrchestrationService crashed and restarted, what would happen to the three recurring Hangfire jobs? Where are they re-registered? (See `Program.cs` lines 79–92.)

---

## Common mistakes

- **Calling `/health/ready` as the liveness probe.** Liveness must always return 200 if the process is alive. Readiness checks SQL and may return 503 (e.g., when Azure SQL auto-pauses). This exact bug caused a container restart loop documented in [`AUDIT_REPORT.md`](./AUDIT_REPORT.md).

- **Treating the MongoDB landing zone as a message queue.** MongoDB `listings_raw` has no acknowledgment semantics, no ordering guarantees, no dead-letter support. The `processed: false` flag pattern is a polling cursor, not a queue. For production at scale, replace this with Kafka or Azure Service Bus.

- **Cross-service database coupling as an anti-pattern.** AnalyticsApi and OrchestrationService share a SQL Server database. In strict DDD microservices each service owns its datastore exclusively. The tradeoff here is pragmatic: shared DB = simpler queries, explicit sync overhead avoided, fine for a portfolio project.

- **Assuming microservices = better.** The architecture is justified for this portfolio's goal. In production contexts, "monolith first, extract when you feel the pain" is almost always correct.

- **Forgetting Hangfire's SQL dependency.** OrchestrationService has two SQL connections: EF Core for warehouse tables, Hangfire's ADO.NET client for job storage. Both use `SQL_CONNECTION_STRING`, both fail the same way if that env var is wrong.

---

## Interview Angle — Smart Apartment Data

1. **"Why did you choose microservices?"** — SLA isolation (AI calls must not block warehouse reads), independent scaling, and demonstrating distributed systems fluency. I'd start a real two-person project as a monolith and extract services only when an SLA boundary or scaling need justified the complexity.

2. **"What is the medallion architecture?"** — Bronze = MongoDB landing (raw, unvalidated). Silver = SQL Server warehouse (cleaned, typed). Gold = fact_market_metrics and fact_anomaly (pre-aggregated, query-ready). The pattern isolates failure at ingest time from query time.

3. **"How does InsightsService find AnalyticsApi?"** — Via `AnalyticsApi:BaseUrl` config, set as an env var in Bicep. The actual call is in `src/MDH.InsightsService/Features/MarketSummaryEndpoint.cs` line 15.

4. **"What would you change in production?"** — VNet integration to take SQL/Cosmos off public internet; managed identity instead of connection string secrets; OpenTelemetry distributed traces; a real queue (Kafka/Service Bus) between ingestion and orchestration.

5. **30-second talking point:** "Data flows from a synthetic generator into MongoDB as a raw landing zone, through Hangfire ETL jobs into a SQL Server star schema, out through a CQRS REST API, and optionally through Anthropic Claude for AI summarization. Every service boundary buys something: the AI service is isolated so a Claude outage can't take down warehouse queries; the landing zone accepts any schema so no raw data is ever lost."

6. **Job requirement proof:** "Microservices" requirement — 4 independently containerized, independently deployed services with separate health checks, Dockerfiles, and Container App resources in Azure.
