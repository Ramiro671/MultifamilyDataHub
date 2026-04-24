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
| Cloud (AWS / Azure) | Dockerfiles + `docs/CLOUD_DEPLOYMENT.md` with ECS Fargate and Azure Container Apps |
| AI tools in workflow | `MDH.InsightsService` calls Anthropic Claude for market summaries & anomaly explanations |
| Debug / troubleshoot | `🔴 BREAKPOINT` comments at all critical paths; debugger guide in `docs/LOCAL_SETUP.md` |
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

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the full diagram and per-service breakdown.

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

## Quickstart

See **[docs/LOCAL_SETUP.md](docs/LOCAL_SETUP.md)** for the full setup walkthrough.

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

## License

MIT © Ramiro Lopez
