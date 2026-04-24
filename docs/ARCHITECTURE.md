# Architecture — MultifamilyDataHub

## Elevator Pitch

MultifamilyDataHub is a production-grade backend microservices platform that ingests, curates, and analyzes synthetic multifamily rental listing data across 12 US submarkets. It demonstrates a complete data engineering pipeline — from raw MongoDB landing zone through SQL Server star-schema warehouse to AI-powered market insights via Claude — using C# 13 / .NET 9, EF Core 9, Hangfire, MediatR, and Polly.

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
│  │  Bogus synthetic data    │────▶│  CleanListingsJob (30s)            │ │
│  │  1000 listings/tick      │     │  BuildMarketMetricsJob (5min)      │ │
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

## Service Details

### MDH.IngestionService
**Purpose:** Continuous synthetic data generator that simulates a real-world listing scraper.

**Responsibilities:**
- Generates 1,000 realistic rental listings per tick (every 10 seconds) using the Bogus library
- Covers 12 major US multifamily submarkets with market-appropriate rent distributions
- Writes raw BSON documents to MongoDB `mdh_raw.listings_raw` (the "landing zone")
- Exposes `/health` on port 5010

**Inbound:** Configuration (interval, MongoDB URI)
**Outbound:** MongoDB `listings_raw` collection

**Why separate service:** Decouples the rate of data arrival from transformation complexity. A real scraper would be an external system; this service simulates it.

---

### MDH.OrchestrationService
**Purpose:** ETL orchestrator that moves data from the raw landing zone to the curated warehouse.

**Responsibilities:**
- Runs three Hangfire recurring jobs:
  - `CleanListingsJob` (30s): Pulls unprocessed MongoDB docs → normalizes → upserts `dim_listing` + inserts `fact_daily_rent`
  - `BuildMarketMetricsJob` (5min): Aggregates daily rent facts into `fact_market_metrics` by submarket/bedroom band
  - `DetectAnomaliesJob` (hourly): Applies 3-sigma rule per submarket/bedroom band → flags outliers in `fact_anomaly`
- Manages EF Core Code First migrations (star schema)
- Exposes Hangfire dashboard at `/hangfire` on port 5020

**Inbound:** MongoDB (raw listings), SQL Server connection
**Outbound:** SQL Server warehouse tables

**Why separate service:** Job scheduling, ETL, and warehouse maintenance are operationally distinct from serving API requests. Separate deployment allows independent scaling and restartability.

---

### MDH.AnalyticsApi
**Purpose:** Read-only REST API that surfaces curated warehouse data.

**Responsibilities:**
- CQRS: all reads via MediatR query handlers
- Endpoints: market list, historical metrics, comparables, listing search (paginated), anomalies, listing detail
- JWT Bearer authentication (HS256)
- Swagger documentation at `/swagger`
- Health checks at `/health` and `/health/ready`
- Serilog structured request logging

**Inbound:** HTTP (consumers), JWT tokens
**Outbound:** SQL Server (read-only via EF Core `AsNoTracking()`)

**Why separate service:** Clean API/write separation. The analytics queries can be independently scaled and cached without affecting ETL throughput.

---

### MDH.InsightsService
**Purpose:** AI-augmented analysis layer that transforms structured data into natural-language insights.

**Responsibilities:**
- Fetches market metrics or listing data from AnalyticsApi
- Composes prompt templates (Markdown files in `Prompts/`) into Claude API calls
- Returns natural-language summaries with embedded statistics
- Polly retry (3 attempts, exponential backoff) + circuit breaker (5 faults / 30s) on the Anthropic HttpClient
- Same JWT auth as AnalyticsApi

**Inbound:** HTTP (consumers), Anthropic API key
**Outbound:** MDH.AnalyticsApi (HTTP), Anthropic `claude-sonnet-4-20250514`

**Why separate service:** AI calls are slow, expensive, and have different SLA requirements than fast warehouse queries. Circuit breaker isolation prevents Anthropic availability from impacting the core API.

---

## End-to-End Data Flow

```
1. INGEST: IngestionService generates synthetic listings every 10s
           → Writes raw BSON to MongoDB mdh_raw.listings_raw (Processed=false)

2. CLEAN: CleanListingsJob (every 30s) reads unprocessed MongoDB docs
          → Trims/normalizes fields, deduplicates by external_id
          → Upserts warehouse.dim_listing
          → Inserts warehouse.fact_daily_rent
          → Marks MongoDB docs as Processed=true

3. AGGREGATE: BuildMarketMetricsJob (every 5min) reads fact_daily_rent
              → Groups by submarket + bedrooms
              → Computes avg/median rent, rent/sqft, occupancy estimate
              → Upserts warehouse.fact_market_metrics

4. ANOMALY: DetectAnomaliesJob (hourly) reads fact_daily_rent
            → Per submarket/bedroom band: computes mean + std dev
            → Flags listings with |z-score| >= 3.0
            → Inserts warehouse.fact_anomaly

5. QUERY: AnalyticsApi reads warehouse via EF Core (read-only)
          → Returns structured JSON to consumers

6. INSIGHT: InsightsService calls AnalyticsApi → builds prompt
            → Sends to Anthropic Claude → returns natural-language insight
```

---

## Architectural Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Microservices vs monolith | Microservices (4 services) | Independent deployment, SLA separation, demonstrates distributed systems skills |
| Star schema | dim_* + fact_* in SQL Server | Industry standard for analytics; optimized for aggregation queries |
| Hangfire vs Quartz.NET | Hangfire | Better ASP.NET Core integration, built-in dashboard, simpler SQL Server storage |
| MongoDB for raw landing zone | MongoDB | Schema-flexible for raw scraped data; allows ingestion without knowing final shape |
| CQRS in AnalyticsApi | MediatR | Clean separation of read models from write concerns; demonstrates enterprise patterns |
| Polly in InsightsService | Circuit breaker + retry | Anthropic API can be slow/rate-limited; prevents cascading failures |
| Manual migration vs dotnet ef | Handwritten migration | CI doesn't have a database; handwritten migration runs from code on startup |
