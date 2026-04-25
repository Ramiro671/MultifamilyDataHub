# MultifamilyDataHub

> A production-grade **backend microservices platform** for multifamily rental market analytics — built to demonstrate every skill listed in the Smart Apartment Data Backend C# Developer job description.

---

## What This Project Demonstrates

This repository shows end-to-end backend engineering across the full stack of a modern data platform:

| Job Requirement | Implementation |
|---|---|
| REST API design with C# | `MDH.AnalyticsApi` — 6 Minimal API endpoints, MediatR CQRS, JWT Bearer auth, Swagger |
| C# 13 / .NET 9 | Entire stack pinned via `global.json`, `LangVersion=latest` |
| SQL Server | Star-schema warehouse with EF Core 9 migrations (`warehouse.*`) |
| NoSQL (MongoDB) | Raw landing zone at `mdh_raw.listings_raw` |
| Big Data Orchestration | Hangfire recurring jobs: 30s ETL, 5min aggregations, hourly anomaly detection |
| Data Warehousing | `dim_listing`, `dim_submarket`, `fact_daily_rent`, `fact_market_metrics`, `fact_anomaly` |
| Large datasets / pipelines | 1,000 synthetic listings per tick × continuous → multi-million row ETL |
| Microservices | 4 independently deployable services + shared library |
| Cloud (AWS / Azure) | Dockerfiles + [`docs/14_CLOUD_FUNDAMENTALS.md`](docs/14_CLOUD_FUNDAMENTALS.md) with ECS Fargate and Azure Container Apps |
| AI tools in workflow | `MDH.InsightsService` calls Anthropic Claude for market summaries & anomaly explanations |
| Debug / troubleshoot | `🔴 BREAKPOINT` comments at all critical paths; debugger guide in [`docs/12_LOCAL_SETUP.md`](docs/12_LOCAL_SETUP.md) |
| Unit tests | xUnit + Moq + FluentAssertions — 23 tests across 3 test projects |
| CI/CD | GitHub Actions build + test pipeline on push and PR |

---

## Architecture

```
Bogus Synthetic Data
        │
        ▼
MDH.IngestionService ──▶ MongoDB (Landing Zone)
                                │
                                ▼
        MDH.OrchestrationService (Hangfire ETL)
                                │
                                ▼
                     SQL Server Warehouse (Star Schema)
                                │
                                ▼
              MDH.AnalyticsApi (REST CQRS, JWT, Swagger)
                                │
                                ▼
            MDH.InsightsService ──▶ Anthropic Claude API
```

See [`docs/02_ARCHITECTURE.md`](docs/02_ARCHITECTURE.md) for the full diagram and per-service breakdown.

---

## Tech Stack

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)
![C# 13](https://img.shields.io/badge/C%23-13-239120?logo=csharp)
![SQL Server](https://img.shields.io/badge/SQL%20Server-2022-CC2927?logo=microsoftsqlserver)
![MongoDB](https://img.shields.io/badge/MongoDB-7-47A248?logo=mongodb)
![Docker](https://img.shields.io/badge/Docker-Compose-2496ED?logo=docker)
![Hangfire](https://img.shields.io/badge/Hangfire-1.8-blue)
![MediatR](https://img.shields.io/badge/MediatR-12-purple)
![Polly](https://img.shields.io/badge/Polly-8-green)
![xUnit](https://img.shields.io/badge/xUnit-2.9-yellow)
![GitHub Actions](https://img.shields.io/badge/GitHub_Actions-CI-2088FF?logo=githubactions)

---

## Deploy to Azure (free tier)

Get a live URL in under 30 minutes — runs at **$0–$2/month** with scale-to-zero.

1. Copy and fill `infra/main.parameters.json` (gitignored — never committed)
2. Run `.\deploy\azure\deploy.ps1 -DockerHubUsername <your-username>`
3. Visit the printed URL — Swagger is live

Full guide: **[docs/14a_AZURE_PRODUCTION_DEPLOYMENT.md](docs/14a_AZURE_PRODUCTION_DEPLOYMENT.md)**

---

## Quickstart (Local)

See **[docs/12_LOCAL_SETUP.md](docs/12_LOCAL_SETUP.md)** for the full setup walkthrough.

**TL;DR:**

```bash
git clone https://github.com/your-username/MultifamilyDataHub.git
cd MultifamilyDataHub
cp .env.example .env          # fill in ANTHROPIC_API_KEY and JWT_SECRET
docker compose -f docker/docker-compose.yml up -d
dotnet run --project src/MDH.OrchestrationService  # applies DB migrations
dotnet run --project src/MDH.IngestionService
dotnet run --project src/MDH.AnalyticsApi
dotnet run --project src/MDH.InsightsService
```

Services:
- Hangfire Dashboard: http://localhost:5020/hangfire
- Analytics API Swagger: http://localhost:5030/swagger
- Insights Service Swagger: http://localhost:5040/swagger

---

## Screenshots

_Screenshots will be added after the first running demo._

| Screenshot | Description |
|---|---|
| `docs/img/hangfire-dashboard.png` | Hangfire dashboard showing 3 recurring jobs |
| `docs/img/swagger-analytics.png` | Analytics API Swagger UI with JWT auth |
| `docs/img/swagger-insights.png` | Insights Service endpoints |
| `docs/img/anomaly-chart.png` | Anomaly detection results visualization |

---

## AI Integration Highlight

`MDH.InsightsService` is the AI-powered layer of the platform. It:

1. Fetches structured market data from `MDH.AnalyticsApi`
2. Loads **prompt templates** from [`src/MDH.InsightsService/Prompts/`](src/MDH.InsightsService/Prompts/) (Markdown files that make the AI integration transparent and auditable)
3. Sends the combined context to **Anthropic `claude-sonnet-4-20250514`**
4. Returns natural-language market summaries and anomaly explanations

The Anthropic `HttpClient` is wrapped in a **Polly** pipeline (3-attempt exponential backoff + circuit breaker) so Anthropic API degradation never cascades to the core analytics API.

See:
- [`src/MDH.InsightsService/Prompts/market-summary.md`](src/MDH.InsightsService/Prompts/market-summary.md)
- [`src/MDH.InsightsService/Prompts/listing-explain.md`](src/MDH.InsightsService/Prompts/listing-explain.md)
- [`src/MDH.InsightsService/Anthropic/AnthropicClient.cs`](src/MDH.InsightsService/Anthropic/AnthropicClient.cs)

---

## Documentation

Numbered study curriculum covering every concept in this codebase — structured for interview preparation.

| # | File | What it covers |
|---|---|---|
| 00 | [00_INDEX.md](docs/00_INDEX.md) | Table of contents, reading paths, glossary pointer |
| 01 | [01_PROJECT_STRUCTURE.md](docs/01_PROJECT_STRUCTURE.md) | Solution layout, `global.json`, CPM, `Directory.Build.props` |
| 02 | [02_ARCHITECTURE.md](docs/02_ARCHITECTURE.md) | End-to-end data flow, microservices tradeoffs, medallion pattern |
| 03 | [03_DATA_WAREHOUSING_SQLSERVER.md](docs/03_DATA_WAREHOUSING_SQLSERVER.md) | Star schema, dim/fact tables, EF Core, index strategy |
| 04 | [04_NOSQL_LANDING_ZONE_MONGODB.md](docs/04_NOSQL_LANDING_ZONE_MONGODB.md) | MongoDB landing zone, schema-on-read, driver patterns |
| 05 | [05_DATA_DICTIONARY.md](docs/05_DATA_DICTIONARY.md) | Every SQL table and MongoDB document shape, domain glossary |
| 06 | [06_MDH_SHARED.md](docs/06_MDH_SHARED.md) | Shared library, domain primitives, `Result<T>`, contracts |
| 07 | [07_MDH_INGESTIONSERVICE.md](docs/07_MDH_INGESTIONSERVICE.md) | `BackgroundService`, Bogus faker, MongoDB writes |
| 08 | [08_MDH_ORCHESTRATIONSERVICE.md](docs/08_MDH_ORCHESTRATIONSERVICE.md) | Hangfire, ETL jobs, 3-sigma anomaly detection |
| 09 | [09_MDH_ANALYTICSAPI.md](docs/09_MDH_ANALYTICSAPI.md) | MediatR CQRS, Minimal API, JWT auth, EF Core reads |
| 10 | [10_MDH_INSIGHTSSERVICE.md](docs/10_MDH_INSIGHTSSERVICE.md) | Anthropic client, Polly retry/circuit breaker, prompt engineering |
| 11 | [11_REST_API_DESIGN_CSHARP.md](docs/11_REST_API_DESIGN_CSHARP.md) | Richardson Maturity Model, HTTP semantics, ProblemDetails |
| 12 | [12_LOCAL_SETUP.md](docs/12_LOCAL_SETUP.md) | Prerequisite install, Docker Compose, debugger attach, curl examples |
| 13 | [13_DEBUGGING_TROUBLESHOOTING.md](docs/13_DEBUGGING_TROUBLESHOOTING.md) | VS/VS Code debugger, breakpoint map, 4 bug post-mortems |
| 14 | [14_CLOUD_FUNDAMENTALS.md](docs/14_CLOUD_FUNDAMENTALS.md) | IaaS/PaaS, containers, probes, 12-factor, secrets management |
| 14a | [14a_AZURE_PRODUCTION_DEPLOYMENT.md](docs/14a_AZURE_PRODUCTION_DEPLOYMENT.md) | Bicep, Container Apps, free-tier deployment walkthrough |
| 14b | [14b_DEVOPS_AND_SUPPLY_CHAIN.md](docs/14b_DEVOPS_AND_SUPPLY_CHAIN.md) | Multi-stage Docker, GitHub Actions CI, image tagging, SBOM |
| 15 | [15_INTERVIEW_PREP.md](docs/15_INTERVIEW_PREP.md) | 31 Q&A pairs, elevator pitch, system design, salary anchor |

---

## License

MIT © Ramiro Lopez
