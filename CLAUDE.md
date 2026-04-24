# MultifamilyDataHub — Backend Portfolio for Smart Apartment Data

You are Claude Code operating inside the repository root. Execute the full plan below **without asking for confirmation**. Only stop if a command is permanently destructive to files outside this repo.

---

## 0. Identity & Commit Policy (NON-NEGOTIABLE)

Git identity is already configured **locally** in this repository:

- `user.name` = `Ramiro Lopez`
- `user.email` = `rammirolopez@gmail.com`

**Rules:**
- Every commit MUST be authored by that identity. Verify with `git log --format="%an <%ae>"` after each commit.
- NEVER run `git config --global` or `git config --system`. Local only.
- Make small, **Conventional Commits**: `feat:`, `fix:`, `docs:`, `chore:`, `test:`, `refactor:`, `ci:`.
- One commit per logical unit (don't squash the whole thing into one).
- Do NOT push to a remote. Stop at local commits. Ramiro will add the remote manually.

---

## 1. Mission

Build a production-grade **backend microservices platform** that demonstrates every skill listed in the Smart Apartment Data Backend C# Developer job description. Domain: **multifamily rental market analytics** (mirrors their business), using **synthetic data** generated with Bogus.

Every requirement below is a hard requirement unless marked *optional*:

| Job requirement | Hit by |
|---|---|
| REST API design with C# | `MDH.AnalyticsApi` + `MDH.InsightsService` |
| C# 13 / .NET 9 | Entire stack pinned via `global.json` |
| SQL Server | Curated warehouse in `MDH.OrchestrationService` / `MDH.AnalyticsApi` |
| NoSQL | MongoDB raw landing zone in `MDH.IngestionService` |
| Big Data Orchestration | Hangfire recurring jobs in `MDH.OrchestrationService` |
| Data Warehousing | Star schema (`dim_*` / `fact_*`) in SQL Server |
| Large datasets / data pipelines | Ingestion generates 1000+ records/tick; ETL handles multi-million row tables |
| Microservices | 4 independently deployable services + shared library |
| Cloud (AWS/Azure) | Dockerfiles + `docs/CLOUD_DEPLOYMENT.md` with AWS ECS + Azure ACA paths |
| AI tools in workflow (PLUS) | `MDH.InsightsService` calls Anthropic Claude API for market summaries and anomaly explanations |
| Debug / troubleshoot / work independently | `🔴 BREAKPOINT` comments placed at key paths; `docs/LOCAL_SETUP.md` explains how to attach debugger |
| Unit tests | xUnit + Moq + FluentAssertions, 3+ tests per service |
| CI/CD | GitHub Actions build + test pipeline |

---

## 2. Target Stack (pinned)

- **.NET 9 SDK** (pin via `global.json`)
- **C# 13**, `<LangVersion>latest</LangVersion>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`
- **SQL Server 2022** (via Docker `mcr.microsoft.com/mssql/server:2022-latest`)
- **MongoDB 7** (via Docker `mongo:7`)
- **EF Core 9** (SQL Server provider), **MongoDB.Driver 3.x**
- **Hangfire 1.8.x** with SQL Server storage
- **Serilog** structured logging (Console + File sinks)
- **MediatR 12**, **FluentValidation 11**, **Polly 8**, **Bogus 35**
- **xUnit**, **Moq**, **FluentAssertions**
- **Anthropic SDK**: use `Anthropic.SDK` NuGet or raw `HttpClient` to `https://api.anthropic.com/v1/messages`, model `claude-sonnet-4-20250514`
- **Docker**, **docker-compose v2**
- **JWT Bearer** auth with symmetric key from `.env`

Use `Directory.Packages.props` for centralized NuGet version management.

---

## 3. Repository Layout (build exactly this)

```
MultifamilyDataHub/
├── .github/workflows/ci.yml
├── .claude/settings.json                  (already exists, do not modify)
├── src/
│   ├── MDH.Shared/                        (class library: DTOs, contracts, common)
│   ├── MDH.IngestionService/              (.NET 9 Worker → MongoDB raw)
│   ├── MDH.OrchestrationService/          (.NET 9 Worker + Hangfire → SQL Server)
│   ├── MDH.AnalyticsApi/                  (ASP.NET Core Web API, REST, reads SQL)
│   └── MDH.InsightsService/               (ASP.NET Core Web API, Anthropic-powered)
├── tests/
│   ├── MDH.AnalyticsApi.Tests/
│   ├── MDH.OrchestrationService.Tests/
│   └── MDH.Shared.Tests/
├── docker/
│   ├── docker-compose.yml                 (sqlserver + mongo + hangfire dashboard)
│   └── init-db/                           (SQL init scripts)
├── docs/
│   ├── ARCHITECTURE.md
│   ├── DATA_DICTIONARY.md
│   ├── LOCAL_SETUP.md
│   └── CLOUD_DEPLOYMENT.md
├── global.json                            (pin .NET 9)
├── Directory.Packages.props               (central NuGet)
├── Directory.Build.props                  (LangVersion, Nullable, ImplicitUsings)
├── MultifamilyDataHub.sln
├── CLAUDE.md                              (this file)
├── README.md
├── .gitignore                             (already exists, extend if needed)
├── .env.example                           (already exists)
└── .editorconfig
```

---

## 4. Service specifications

### 4.1 MDH.Shared
- Domain primitives: `Money`, `Submarket`, `ListingId`, `BedBath`
- DTOs: `ListingDto`, `MarketMetricsDto`, `AnomalyDto`, `InsightRequestDto`, `InsightResponseDto`
- Contracts: `IListingRepository`, `IRawListingStore`, `IInsightClient`
- Common result/error types: `Result<T>` pattern

### 4.2 MDH.IngestionService (Worker)
- `BackgroundService` that runs every 10s (configurable via `Ingestion:IntervalSeconds`)
- Uses **Bogus** to fabricate 1000 synthetic listings per tick across 12 submarkets (Austin, Houston, Dallas, Phoenix, Atlanta, Denver, Miami, Nashville, Tampa, Orlando, Raleigh, Charlotte)
- Each listing has: external id, submarket, street address, unit, bedrooms, bathrooms, sqft, asking rent, effective rent, concessions, operator, scraped_at
- Writes raw BSON to MongoDB collection `mdh_raw.listings_raw`
- Emits Serilog structured logs with correlation id
- Exposes `/health` endpoint on port 5010
- Place `// 🔴 BREAKPOINT: raw batch persisted` in `RawListingStore.InsertManyAsync`

### 4.3 MDH.OrchestrationService (Worker + Hangfire)
- Hangfire server with SQL Server storage (schema `hangfire`)
- Hangfire dashboard at `http://localhost:5020/hangfire` (no auth for local)
- Three recurring jobs:
  1. `CleanListingsJob` — every 30s: pulls unprocessed raw docs → normalizes (trim, dedupe by external id, unit conversion) → upserts `warehouse.dim_listing` + inserts `warehouse.fact_daily_rent`
  2. `BuildMarketMetricsJob` — every 5 min: aggregates `fact_daily_rent` by submarket/bedrooms → `warehouse.fact_market_metrics` (avg rent, median rent, rent_per_sqft, occupancy estimate, sample_size)
  3. `DetectAnomaliesJob` — hourly: flags listings with rent outside 3σ of submarket/bed band → `warehouse.fact_anomaly`
- EF Core Code First migrations in `Persistence/Migrations`
- Star schema SQL: `warehouse.dim_listing`, `warehouse.dim_submarket`, `warehouse.fact_daily_rent`, `warehouse.fact_market_metrics`, `warehouse.fact_anomaly`
- Place `// 🔴 BREAKPOINT: starting ETL batch` in each job handler entrypoint.

### 4.4 MDH.AnalyticsApi (ASP.NET Core Web API, .NET 9)
- Minimal API + MediatR handlers, **CQRS** pattern
- Endpoints (all `/api/v1/*`, all return `application/json`, all OpenAPI-documented):
  - `GET /markets` → list submarkets with latest metrics
  - `GET /markets/{submarket}/metrics?from={iso}&to={iso}` → historical market metrics
  - `GET /markets/{submarket}/comps?bedrooms={n}&sqftMin={n}&sqftMax={n}` → comparables
  - `GET /listings/search?submarket=&bedrooms=&minRent=&maxRent=&page=&pageSize=` → paginated search
  - `GET /listings/anomalies?submarket=&since={iso}` → flagged listings
  - `GET /listings/{id}` → single listing detail
- JWT Bearer auth required on all `/api/v1/*` (HS256, secret from env)
- Swagger at `/swagger` exposing the spec
- Health check at `/health` and `/health/ready`
- Serilog request logging, ProblemDetails for errors
- Port 5030
- Place `// 🔴 BREAKPOINT: query received` in each endpoint handler.

### 4.5 MDH.InsightsService (ASP.NET Core Web API) — the AI PLUS
- Port 5040
- Endpoints:
  - `POST /api/v1/insights/market-summary` — body `{ submarket, fromDate, toDate }` → calls AnalyticsApi for metrics → composes a careful system prompt → calls Anthropic `claude-sonnet-4-20250514` → returns natural-language market summary with key stats (avg rent delta, rent/sqft trend, inventory shift) plus the raw stats
  - `POST /api/v1/insights/listing-explain` — body `{ listingId }` → fetches the listing + anomaly context → asks Claude to explain in plain English *why* this listing stands out (rent vs submarket, concessions, etc.)
- Uses typed `HttpClient` with **Polly** retry (3 attempts, exponential backoff) and circuit breaker
- Reads `ANTHROPIC_API_KEY` from env
- Structured prompt templates in `Prompts/` folder as Markdown — this makes it obvious to a reviewer how AI is integrated (the PLUS the job description mentions)
- Place `// 🔴 BREAKPOINT: prompt assembled, sending to Claude` just before the HTTP call.

---

## 5. Tests (minimum, per project)

- `MDH.Shared.Tests`: `Money` arithmetic, `Result<T>` behavior
- `MDH.AnalyticsApi.Tests`: handler tests with in-memory EF Core + Moq for repositories; one integration test against `WebApplicationFactory`
- `MDH.OrchestrationService.Tests`: `CleanListingsJob` transformation test with mocked repos; anomaly detector math test

All tests AAA pattern. Use `FluentAssertions`. Run locally and in CI.

---

## 6. docker-compose (local infra)

Bring up `sqlserver`, `mongo`, and seed credentials matching `.env.example`. Expose 1433 and 27017. Persist volumes under `./docker/.volumes/` (gitignored).

---

## 7. CI pipeline (`.github/workflows/ci.yml`)

- Trigger: push to main / develop, and PR
- Steps: setup-dotnet 9 → `dotnet restore` → `dotnet build -c Release --no-restore` → `dotnet test --no-build --verbosity normal`
- No deploy step. Build/test only.

---

## 8. Required documentation (docs/)

Each of these is a deliverable. Write them as you go (don't leave for the end):

### 8.1 `docs/ARCHITECTURE.md`
- One-paragraph elevator pitch
- ASCII or Mermaid diagram of the 4 services + SQL + Mongo + Anthropic API
- Per-service section explaining: purpose, responsibilities, inbound/outbound dependencies, why it exists as a separate service
- End-to-end data flow: raw ingest → curated warehouse → API → AI insight
- Architectural decisions list (microservices vs monolith, star schema, Hangfire over Quartz, etc.)

### 8.2 `docs/DATA_DICTIONARY.md`
- Every SQL table: columns, types, description, PK/FK
- MongoDB collection document shape with JSON example
- **Reserved domain words** section explaining: *submarket*, *asking rent*, *effective rent*, *concession*, *operator*, *dim table*, *fact table*, *landing zone*, *curated zone*, *star schema*, *grain*
- **Acronyms** section: MDH, ETL, ELT, DW, SLA, MQL, ADR, CQRS, DDD, PK, FK, TTL, RBAC

### 8.3 `docs/LOCAL_SETUP.md`
- Prerequisites: .NET 9 SDK, Docker Desktop, Git, (optional) Visual Studio 2022 17.12+ or VS Code + C# Dev Kit
- Step-by-step: clone → `cp .env.example .env` → fill `ANTHROPIC_API_KEY` → `docker compose -f docker/docker-compose.yml up -d` → `dotnet ef database update` per project → `dotnet run --project src/...` in separate terminals
- How to attach the debugger in Visual Studio (Debug → Attach to Process → pick the dotnet process)
- How to attach the debugger in VS Code (launch.json snippet included)
- **Breakpoint map**: list of files + line-identifying comments (`🔴 BREAKPOINT: raw batch persisted`) so the reader can jump straight to the most instructive points
- How to open Swagger, Hangfire dashboard, and hit a sample authenticated request with `curl` (include a working JWT example signed with the demo secret)
- How to regenerate synthetic data

### 8.4 `docs/CLOUD_DEPLOYMENT.md`
- **AWS path (primary)**: ECR for images → ECS Fargate for the 4 services behind an ALB → RDS SQL Server Express → DocumentDB (or Atlas) for Mongo → Secrets Manager for `ANTHROPIC_API_KEY` and JWT secret → CloudWatch Logs. Include `aws ecr create-repository`, `docker build/push`, `aws ecs register-task-definition`, `aws ecs create-service` snippets.
- **Azure path (alternative)**: Azure Container Registry → Azure Container Apps (4 apps) → Azure SQL → Cosmos DB for MongoDB API → Key Vault. Include `az acr build`, `az containerapp create` snippets.
- Health check routing, minimum-viable autoscaling, cost estimate ballpark.

---

## 9. README.md

Top-level project README aimed at a recruiter reviewing the repo. Sections:
1. What this project demonstrates (one paragraph mapping to job requirements)
2. Architecture diagram
3. Tech stack badges
4. Quickstart (link to `docs/LOCAL_SETUP.md`)
5. Screenshots section (leave placeholder images `docs/img/*.png` referenced but do not generate them)
6. AI integration highlight (link to `MDH.InsightsService` and the prompt templates)
7. License: MIT

---

## 10. Execution order (follow exactly)

Commit after each numbered step (small commits, conventional messages):

1. `chore: add global.json, Directory.Build.props, Directory.Packages.props, .editorconfig`
2. `chore: add solution and empty project skeletons for all 5 projects`
3. `chore: add docker-compose with sqlserver and mongo`
4. `feat(shared): add domain primitives, DTOs, contracts, Result<T>`
5. `feat(ingestion): implement Bogus-based synthetic listing generator writing to MongoDB`
6. `feat(orchestration): add EF Core warehouse model and initial migration`
7. `feat(orchestration): implement CleanListingsJob (Mongo → SQL ETL)`
8. `feat(orchestration): implement BuildMarketMetricsJob aggregations`
9. `feat(orchestration): implement DetectAnomaliesJob with 3-sigma rule`
10. `feat(analytics-api): implement REST endpoints with MediatR + EF Core read model`
11. `feat(analytics-api): add JWT auth, Swagger, health checks, Serilog request logging`
12. `feat(insights): implement Anthropic client with Polly, market-summary endpoint`
13. `feat(insights): implement listing-explain endpoint + prompt templates`
14. `test: xUnit tests for shared, analytics-api, orchestration`
15. `ci: add GitHub Actions build + test workflow`
16. `docs: add ARCHITECTURE.md`
17. `docs: add DATA_DICTIONARY.md`
18. `docs: add LOCAL_SETUP.md with debugger instructions and breakpoint map`
19. `docs: add CLOUD_DEPLOYMENT.md (AWS primary, Azure alternative)`
20. `docs: add README.md`

After step 20, run `git log --format="%an <%ae> %s"` and paste the output to confirm every commit is authored by `Ramiro Lopez <rammirolopez@gmail.com>`.

---

## 11. Things you will NOT do

- ❌ Push to any remote
- ❌ Run `git config --global` or `--system`
- ❌ Commit secrets, `.env`, or real API keys
- ❌ Pause to ask "should I continue?" — always continue
- ❌ Invent NuGet packages — if a package name is uncertain, `dotnet package search` first
- ❌ Skip the docs — they are part of the portfolio, not an afterthought

---

Begin now. Start with step 1 of section 10.