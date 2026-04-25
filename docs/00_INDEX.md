# MultifamilyDataHub — Study Curriculum Index

> These docs are lecture notes for a deployed backend system, not marketing material.
> Read them front-to-back to prepare for a mid-level C# backend interview at Smart Apartment Data.

---

## Table of Contents

| # | File | What you will learn | Study time |
|---|---|---|---|
| 00 | `00_INDEX.md` (this file) | Orientation, reading paths, JD mapping | 5 min |
| 01 | [`01_PROJECT_STRUCTURE.md`](./01_PROJECT_STRUCTURE.md) | .NET solution layout, SDK pinning, Central Package Management, MSBuild props | 20 min |
| 02 | [`02_ARCHITECTURE.md`](./02_ARCHITECTURE.md) | Microservices tradeoffs, data-flow, landing vs curated zone, decision table | 25 min |
| 03 | [`03_DATA_WAREHOUSING_SQLSERVER.md`](./03_DATA_WAREHOUSING_SQLSERVER.md) | Star schema, OLAP, EF Core, T-SQL queries, index strategy, SCD | 40 min |
| 04 | [`04_NOSQL_LANDING_ZONE_MONGODB.md`](./04_NOSQL_LANDING_ZONE_MONGODB.md) | MongoDB driver, schema-on-read, TTL indexes, ETL vs ELT, consistency | 25 min |
| 05 | [`05_DATA_DICTIONARY.md`](./05_DATA_DICTIONARY.md) | Every column, domain vocabulary, acronyms | 15 min |
| 06 | [`06_MDH_SHARED.md`](./06_MDH_SHARED.md) | Shared library design, domain primitives, Result\<T\>, contracts | 20 min |
| 07 | [`07_MDH_INGESTIONSERVICE.md`](./07_MDH_INGESTIONSERVICE.md) | BackgroundService lifecycle, Bogus, Mongo write patterns, Serilog | 20 min |
| 08 | [`08_MDH_ORCHESTRATIONSERVICE.md`](./08_MDH_ORCHESTRATIONSERVICE.md) | Hangfire, ETL jobs, 3-sigma math, EF Core migrations, star-schema upserts | 40 min |
| 09 | [`09_MDH_ANALYTICSAPI.md`](./09_MDH_ANALYTICSAPI.md) | Minimal API, CQRS/MediatR, JWT auth, health checks, EF Core read patterns | 35 min |
| 10 | [`10_MDH_INSIGHTSSERVICE.md`](./10_MDH_INSIGHTSSERVICE.md) | Anthropic Messages API, typed HttpClient, Polly policies, prompt engineering | 30 min |
| 11 | [`11_REST_API_DESIGN_CSHARP.md`](./11_REST_API_DESIGN_CSHARP.md) | RMM levels, URI design, HTTP verbs, status codes, versioning, OpenAPI | 30 min |
| 12 | [`12_LOCAL_SETUP.md`](./12_LOCAL_SETUP.md) | Step-by-step local env, Docker, JWT demo, breakpoint map | 15 min |
| 13 | [`13_DEBUGGING_TROUBLESHOOTING.md`](./13_DEBUGGING_TROUBLESHOOTING.md) | VS/VS Code debugger, Serilog query, Hangfire forensics, 4-bug post-mortem case study | 35 min |
| 14 | [`14_CLOUD_FUNDAMENTALS.md`](./14_CLOUD_FUNDAMENTALS.md) | IaaS/PaaS/SaaS, containers vs VMs, 12-factor, liveness vs readiness, observability | 30 min |
| 14a | [`14a_AZURE_PRODUCTION_DEPLOYMENT.md`](./14a_AZURE_PRODUCTION_DEPLOYMENT.md) | Resource inventory, Bicep walkthrough, free-tier math, 5 deployment bugs & lessons | 40 min |
| 14b | [`14b_DEVOPS_AND_SUPPLY_CHAIN.md`](./14b_DEVOPS_AND_SUPPLY_CHAIN.md) | Dockerfile anatomy, multi-stage builds, GitHub Actions CI/CD, image tagging, secrets | 25 min |
| 15 | [`15_INTERVIEW_PREP.md`](./15_INTERVIEW_PREP.md) | 25+ Q&A pairs, elevator pitch, system design, salary, interviewer questions | 45 min |
| — | [`AUDIT_REPORT.md`](./AUDIT_REPORT.md) | Full compliance audit + production deployment post-mortem | reference |

**Total estimated study time: ~6 hours** (cold read), or use a targeted path below.

---

## Reading Paths

### Cold read — full preparation (~4 hours)
Read `00` → `15` in order. Each file builds on the previous. Do not skip 03 and 08; those are the heaviest technical chapters and carry the most interview weight.

### Pre-interview cram (~60 min)
`00` → `02` → `11` → `13` → `14a` → `15`

Focus: architecture overview → REST fundamentals → how you debug → what you actually deployed → scripted answers.

### Cloud and DevOps deep dive (~90 min)
`14` → `14a` → `14b` → `13`

Focus: Azure Container Apps, Bicep, Docker, CI/CD, the 5 deployment bugs, post-mortem.

### Data engineering focus (~90 min)
`03` → `04` → `05` → `07` → `08`

Focus: star schema, MongoDB landing zone, Hangfire ETL jobs, 3-sigma anomaly detection.

---

## Glossary pointer

Domain vocabulary (submarket, asking rent, effective rent, grain, star schema, landing zone) is defined in [`05_DATA_DICTIONARY.md`](./05_DATA_DICTIONARY.md) — reserved domain words section.

---

## JD requirement → doc coverage

| Smart Apartment Data JD requirement | Primary doc | Secondary doc |
|---|---|---|
| REST API design with C# | `09_MDH_ANALYTICSAPI.md` | `11_REST_API_DESIGN_CSHARP.md` |
| C# 13 / .NET 9 | `01_PROJECT_STRUCTURE.md` | `06_MDH_SHARED.md` |
| SQL Server | `03_DATA_WAREHOUSING_SQLSERVER.md` | `08_MDH_ORCHESTRATIONSERVICE.md` |
| NoSQL (MongoDB) | `04_NOSQL_LANDING_ZONE_MONGODB.md` | `07_MDH_INGESTIONSERVICE.md` |
| Big Data Orchestration | `08_MDH_ORCHESTRATIONSERVICE.md` | `02_ARCHITECTURE.md` |
| Data Warehousing (star schema) | `03_DATA_WAREHOUSING_SQLSERVER.md` | `05_DATA_DICTIONARY.md` |
| Large datasets / data pipelines | `08_MDH_ORCHESTRATIONSERVICE.md` | `04_NOSQL_LANDING_ZONE_MONGODB.md` |
| Microservices | `02_ARCHITECTURE.md` | `09_MDH_ANALYTICSAPI.md` |
| Cloud (AWS / Azure) | `14_CLOUD_FUNDAMENTALS.md` | `14a_AZURE_PRODUCTION_DEPLOYMENT.md` |
| AI tools in workflow (PLUS) | `10_MDH_INSIGHTSSERVICE.md` | `15_INTERVIEW_PREP.md` |
| Debug / troubleshoot / independently | `13_DEBUGGING_TROUBLESHOOTING.md` | `AUDIT_REPORT.md` |
| Unit tests | `06_MDH_SHARED.md` | `09_MDH_ANALYTICSAPI.md` |
| CI/CD | `14b_DEVOPS_AND_SUPPLY_CHAIN.md` | `14a_AZURE_PRODUCTION_DEPLOYMENT.md` |
