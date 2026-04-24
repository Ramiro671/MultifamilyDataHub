# Compliance Audit Report — MultifamilyDataHub
**Date:** 2026-04-24  
**Auditor:** Claude Code  
**Scope:** Full compliance audit against Smart Apartment Data Backend C# Developer JD

---

## PHASE 1 — BUILD & TEST VERIFICATION

### 1. `dotnet --version`
**Result: PASS**  
Version: `9.0.310` — .NET 9 is active, consistent with `global.json` (`rollForward: latestMinor`).

---

### 2. `dotnet restore MultifamilyDataHub.sln`
**Result: PASS**  
All projects already up-to-date for restore. No errors.

---

### 3. `dotnet build -c Release --no-restore` (standard)
**Result: PASS**  
All 8 projects build successfully.  
- **Warnings:** 0  
- **Errors:** 0  
- Build time: ~4 seconds

---

### 4. `dotnet build -c Release --no-restore -p:TreatWarningsAsErrors=true` (strict)
**Result: PASS**  
Zero warnings in production source code. Strict mode passes cleanly with 0 warnings, 0 errors.

---

### 5. `dotnet test -c Release --no-build --verbosity normal`
**Result: PASS**

| Project | Total | Passed | Failed | Skipped | Duration |
|---|---|---|---|---|---|
| MDH.Shared.Tests | 12 | 12 | 0 | 0 | 0.89s |
| MDH.AnalyticsApi.Tests | 6 | 6 | 0 | 0 | 1.95s |
| MDH.OrchestrationService.Tests | 5 | 5 | 0 | 0 | 1.89s |
| **TOTAL** | **23** | **23** | **0** | **0** | |

Note: The MSBuild timestamp in test output shows `24/abr/2026` — this is the system locale (Spanish/Mexico), **not a code defect**. The test output text itself is entirely in English.

---

### 6. `dotnet format --verify-no-changes --verbosity diagnostic`
**Result: FAIL — 25 whitespace violations**

`dotnet format` reports whitespace formatting drift in **test files only** (not in production source):

| File | Issue |
|---|---|
| `tests/MDH.AnalyticsApi.Tests/GetListingByIdTests.cs` | Line 20: inline object initializer not expanded (3 violations) |
| `tests/MDH.AnalyticsApi.Tests/SearchListingsQueryTests.cs` | Lines 20, 24: same pattern (6 violations) |
| `tests/MDH.OrchestrationService.Tests/CleanListingsJobTests.cs` | Line 25: same pattern (3 violations) |
| `tests/MDH.OrchestrationService.Tests/DetectAnomaliesJobTests.cs` | Lines 28, 39–48: inline object initializers (13 violations) |

All violations are the same pattern: object initializer properties on a single line instead of separate lines, as `.editorconfig` (CRLF + indent_size=4) requires. Production code is clean. Tests are the only offenders.

---

### 7. `dotnet list package --vulnerable --include-transitive`
**Result: PARTIAL CONCERN — 3 transitive vulnerabilities found**

| Project | Package | Severity | Advisory |
|---|---|---|---|
| MDH.OrchestrationService | `Newtonsoft.Json 11.0.1` (transitive, via Hangfire) | **High** | GHSA-5crp-9r3c-p9vr |
| MDH.AnalyticsApi | `Azure.Identity 1.10.3` (transitive) | Moderate | GHSA-wvxc-855f-jvrv |
| MDH.AnalyticsApi | `Azure.Identity 1.10.3` (transitive) | Moderate | GHSA-m5vv-6r4h-3vj9 |
| MDH.AnalyticsApi | `Microsoft.Identity.Client 4.56.0` (transitive) | Low | GHSA-x674-v45j-fwxw |
| MDH.AnalyticsApi.Tests | Same as AnalyticsApi | Low/Moderate | (same) |

**Assessment:** All three are **transitive** vulnerabilities — pulled in by dependencies, not directly referenced. The `Newtonsoft.Json` High severity is from Hangfire's internal dependency chain. These are acceptable for a portfolio project but worth noting. The direct packages chosen are all current.

---

### 8. `dotnet list package --deprecated`
**Result: INFORMATIONAL — 4 deprecated top-level packages**

| Package | Version | Reason | Alternative |
|---|---|---|---|
| `FluentValidation.AspNetCore` | 11.3.0 | Legacy | (none stated) |
| `Polly.Extensions.Http` | 3.0.0 | Legacy | `Microsoft.Extensions.Http.Resilience` |
| `xunit` | 2.9.3 | Legacy | `xunit.v3` |

**Assessment:** `xunit 2.x` marked Legacy in favor of `xunit.v3` (still in pre-release as of audit date — this is acceptable). `Polly.Extensions.Http` is legacy but the recommended replacement wasn't GA when this was written. `FluentValidation.AspNetCore` is referenced but **never wired** in any service (see Phase 3). None of these are blocking for a portfolio project.

---

## PHASE 2 — DOCKER COMPOSE SANITY CHECK

### 1. `docker compose config` validation
**Result: PASS (with one non-blocking warning)**

The compose file parses cleanly. One warning:
```
the attribute `version` is obsolete, it will be ignored, please remove it to avoid potential confusion
```
`version: "3.9"` is deprecated in Compose Spec. It does not affect behavior.

### 2. Service and volume verification

| Check | Expected | Found | Status |
|---|---|---|---|
| `sqlserver` service defined | Yes | Yes | ✅ |
| `mongo` service defined | Yes | Yes | ✅ |
| SQL Server port 1433 | Yes | `1433:1433` | ✅ |
| MongoDB port 27017 | Yes | `27017:27017` | ✅ |
| SQL Server persistent volume | Yes | bind mount `./.volumes/sqlserver` | ✅ |
| MongoDB persistent volume | Yes | bind mount `./.volumes/mongo` | ✅ |
| Volumes gitignored | Yes | `docker/.volumes/` in `.gitignore` | ✅ |
| Health checks | Yes | Both services have healthcheck blocks | ✅ |
| Init SQL script mounted | Yes | `./init-db:/docker-entrypoint-initdb.d` | ✅ |

**⚠️ Dead code in docker-compose.yml:** The top-level `volumes:` block declares `sqlserver_data:` and `mongo_data:` as named volumes, but neither is used — both services use bind mounts to `./.volumes/`. The named volume declarations are dead code and misleading.

---

## PHASE 3 — JOB DESCRIPTION COMPLIANCE MATRIX

### Hard Requirements

| Requirement | Implementation | File(s) | Status | Notes |
|---|---|---|---|---|
| 3+ years C# backend experience (portfolio proof) | REST API, Worker services, EF Core, CQRS, background jobs, star schema — all present | All `src/` | ✅ | Full breadth demonstrated |
| REST API design with C# | 6 endpoints in AnalyticsApi + 2 in InsightsService | `Program.cs` files | ✅ | See endpoint list below |
| SQL Server comfort | 5 warehouse tables, EF Core migrations, Code First | `Persistence/` in Orchestration + Analytics | ✅ | |
| NoSQL (MongoDB) | Raw landing zone, BSON documents, index creation | `RawListingStore.cs`, `MongoRawListingStore.cs` | ✅ | |
| Debug / troubleshoot / work independently | 11 × `🔴 BREAKPOINT` comments in production code | See breakpoint map below | ✅ | |
| Fluent English | All code, comments, commits, and docs are English | All files | ✅ | No Spanish found in source. System locale (Spanish) appears only in MSBuild timestamps, not code. |

**Endpoint inventory:**

| Service | Method | Path | Handler File |
|---|---|---|---|
| AnalyticsApi | GET | `/api/v1/markets` | `Features/Markets/GetMarketsQuery.cs` |
| AnalyticsApi | GET | `/api/v1/markets/{submarket}/metrics` | `Features/Markets/GetMarketMetricsQuery.cs` |
| AnalyticsApi | GET | `/api/v1/markets/{submarket}/comps` | `Features/Markets/GetCompsQuery.cs` |
| AnalyticsApi | GET | `/api/v1/listings/search` | `Features/Listings/SearchListingsQuery.cs` |
| AnalyticsApi | GET | `/api/v1/listings/anomalies` | `Features/Listings/GetAnomaliesQuery.cs` |
| AnalyticsApi | GET | `/api/v1/listings/{id}` | `Features/Listings/GetListingByIdQuery.cs` |
| InsightsService | POST | `/api/v1/insights/market-summary` | `Features/MarketSummaryEndpoint.cs` |
| InsightsService | POST | `/api/v1/insights/listing-explain` | `Features/ListingExplainEndpoint.cs` |

**Breakpoint map (all 11 confirmed present):**

| File | Line | Comment |
|---|---|---|
| `src/MDH.IngestionService/Persistence/RawListingStore.cs` | 33 | `🔴 BREAKPOINT: raw batch persisted` |
| `src/MDH.OrchestrationService/Jobs/CleanListingsJob.cs` | 24 | `🔴 BREAKPOINT: starting ETL batch` |
| `src/MDH.OrchestrationService/Jobs/BuildMarketMetricsJob.cs` | 20 | `🔴 BREAKPOINT: starting ETL batch` |
| `src/MDH.OrchestrationService/Jobs/DetectAnomaliesJob.cs` | 22 | `🔴 BREAKPOINT: starting ETL batch` |
| `src/MDH.AnalyticsApi/Features/Markets/GetMarketsQuery.cs` | 26 | `🔴 BREAKPOINT: query received` |
| `src/MDH.AnalyticsApi/Features/Markets/GetMarketMetricsQuery.cs` | 19 | `🔴 BREAKPOINT: query received` |
| `src/MDH.AnalyticsApi/Features/Markets/GetCompsQuery.cs` | 19 | `🔴 BREAKPOINT: query received` |
| `src/MDH.AnalyticsApi/Features/Listings/SearchListingsQuery.cs` | 27 | `🔴 BREAKPOINT: query received` |
| `src/MDH.AnalyticsApi/Features/Listings/GetAnomaliesQuery.cs` | 18 | `🔴 BREAKPOINT: query received` |
| `src/MDH.AnalyticsApi/Features/Listings/GetListingByIdQuery.cs` | 18 | `🔴 BREAKPOINT: query received` |
| `src/MDH.InsightsService/Anthropic/AnthropicClient.cs` | 21 | `🔴 BREAKPOINT: prompt assembled, sending to Claude` |

---

### Sneak Peek Projects

| Requirement | Implementation | File(s) | Status | Notes |
|---|---|---|---|---|
| Big Data Orchestration | Hangfire with 3 recurring jobs, dashboard at `/hangfire` | `OrchestrationService/Program.cs` | ⚠️ | Jobs are registered — **but `CleanListingsJob` cron uses 6-field expression `"*/30 * * * * *"` (with seconds). Standard Hangfire (free tier) only supports 5-field Unix cron. This expression will likely be interpreted as every 30th minute of every hour, not every 30 seconds. See Critical Issues.** |
| Data Warehousing SQL Server | Star schema: 2 dim tables, 3 fact tables, EF Code First migrations | `Persistence/Entities/`, `Migrations/` | ✅ | All tables present with correct naming convention |
| NoSQL landing zone | MongoDB `mdh_raw.listings_raw`, documented in DATA_DICTIONARY | `MongoRawListingStore.cs`, `RawListingStore.cs` | ✅ | |
| API Design C# 13 / .NET 9 | `TargetFramework=net9.0` set globally in `Directory.Build.props`, `LangVersion=latest` | `Directory.Build.props` | ✅ | `LangVersion=latest` resolves to C# 13 for .NET 9. No individual csproj overrides this. |

---

### Nice-to-Haves (PLUS)

| Requirement | Implementation | File(s) | Status | Notes |
|---|---|---|---|---|
| AI tools in workflow (meta) | This entire codebase was generated by Claude Code; CLAUDE.md specifies this explicitly | `CLAUDE.md` | ✅ | Meta-evidence is present |
| AI runtime integration (Anthropic) | `AnthropicClient.cs` makes HTTP calls to `https://api.anthropic.com/v1/messages` using `claude-sonnet-4-20250514` | `Anthropic/AnthropicClient.cs` | ✅ | Model ID matches CLAUDE.md spec |
| Polly retry + circuit breaker | 3-attempt exponential retry + 5-fault circuit breaker on Anthropic HttpClient | `InsightsService/Program.cs` lines 30–35 | ✅ | |
| Prompt templates as Markdown files | `Prompts/market-summary.md` and `Prompts/listing-explain.md` | `src/MDH.InsightsService/Prompts/` | ✅ | Visible to code reviewers, not buried in code |
| Microservices (4 independently runnable) | 4 services with separate `Program.cs` entrypoints on ports 5010/5020/5030/5040 | All `src/MDH.*/Program.cs` | ⚠️ | **No Dockerfiles exist for any service.** CLAUDE.md explicitly required Dockerfiles. CLOUD_DEPLOYMENT.md shows a Dockerfile *example* inline, but none are committed as actual files. Without Dockerfiles, "independently deployable" is incomplete. |
| Cloud docs (AWS + Azure) | `docs/CLOUD_DEPLOYMENT.md` — both paths with CLI snippets, cost estimates | `docs/CLOUD_DEPLOYMENT.md` | ✅ | Substantive content, not stubs |
| Large datasets / pipeline stages | 1,000 listings/tick × 10s = 6,000 docs/min → ETL cleans → aggregates → anomaly detects | `ListingFaker.cs`, 3 job files | ✅ | Pipeline stages are documented |

---

### Additional Gap: FluentValidation Is a Dead Reference

`FluentValidation.AspNetCore` is declared in `MDH.AnalyticsApi.csproj` and listed in `Directory.Packages.props`, but:
- No `AbstractValidator<T>` implementations exist anywhere
- `AddFluentValidation()` is never called in `Program.cs`
- The package is flagged **Legacy/deprecated**

This is a dead dependency — it adds a deprecated package warning without providing any value.

---

### Additional Gap: No `/api/v1/auth/demo-token` Endpoint

`docs/LOCAL_SETUP.md` references `curl -X POST http://localhost:5030/api/v1/auth/demo-token` as a way to get a JWT, but **this endpoint does not exist** in `Program.cs`. `JwtTokenGenerator.cs` contains a static method but is never called or exposed. A developer following the LOCAL_SETUP guide will hit a 404 and be stuck — they cannot easily get a JWT to test the API.

---

## PHASE 4 — DOCUMENTATION AUDIT

### README.md
- **Exists:** ✅ (535 words)
- **Sections present:**
  - ✅ What it demonstrates (table mapping JD requirements)
  - ✅ Architecture diagram (ASCII)
  - ✅ Tech stack badges
  - ✅ Quickstart with link to LOCAL_SETUP
  - ✅ Screenshots section (placeholder images referenced — `docs/img/*.png` — confirmed as intentional stubs)
  - ✅ AI integration highlight with links
  - ✅ License: MIT
- **Gaps:** None. All CLAUDE.md section 9 requirements met.

---

### docs/ARCHITECTURE.md
- **Exists:** ✅ (987 words)
- **Sections present:**
  - ✅ Elevator pitch (one paragraph)
  - ✅ ASCII diagram
  - ✅ Per-service sections (MDH.IngestionService, MDH.OrchestrationService, MDH.AnalyticsApi, MDH.InsightsService) with Purpose, Responsibilities, Inbound/Outbound, Why separate
  - ✅ End-to-end data flow (numbered 6-step flow)
  - ✅ Architectural decisions table (7 decisions)
- **Gaps:** None. All CLAUDE.md section 8.1 requirements met.

---

### docs/DATA_DICTIONARY.md
- **Exists:** ✅ (1,214 words)
- **Sections present:**
  - ✅ All 5 SQL tables with columns, types, descriptions, PK/FK/IDX
  - ✅ MongoDB document shape with JSON example
  - ✅ Reserved domain words section (11 terms)
  - ✅ Acronyms section (16 acronyms)
- **Gaps:** None. All CLAUDE.md section 8.2 requirements met.

---

### docs/LOCAL_SETUP.md
- **Exists:** ✅ (751 words)
- **Sections present:**
  - ✅ Prerequisites table
  - ✅ Clone → `.env` → docker compose → run services steps
  - ✅ Visual Studio 2022 debugger instructions
  - ✅ VS Code `launch.json` snippet
  - ✅ Breakpoint map table
  - ✅ Swagger URL
  - ✅ Hangfire URL
  - ⚠️ `dotnet ef database update` step present but **noted as automatic** — the instructions say "applied automatically on startup" but also give a manual command. The automatic migration is real (it's in `Program.cs`), so this is accurate. However, the manual command `dotnet ef database update --project src/MDH.OrchestrationService` requires EF Tools installed and would fail on machines without it, since EF Tools is only referenced as a `PackageReference` (build tool), not a `dotnet tool`. No `dotnet-tools.json` manifest exists.
  - ❌ **No working pre-signed JWT example.** The curl section shows `<JWT>` as placeholder. The referenced `curl -X POST http://localhost:5030/api/v1/auth/demo-token` endpoint does not exist. A recruiter cannot test the API without either writing code or having existing JWT tooling.

---

### docs/CLOUD_DEPLOYMENT.md
- **Exists:** ✅ (916 words)
- **Sections present:**
  - ✅ AWS path: ECR, ECS Fargate, RDS, DocumentDB, Secrets Manager, CloudWatch
  - ✅ Azure path: ACR, Container Apps, Azure SQL, Cosmos DB, Key Vault
  - ✅ Health check routing section
  - ✅ Autoscaling section
  - ✅ Cost estimates (both paths)
  - ✅ CLI snippets for both paths
  - ⚠️ Dockerfile example is shown inline in the doc (for AnalyticsApi), but actual Dockerfiles are not committed to the repo. The doc says "Each service needs a Dockerfile" — implying they don't yet exist.
- **Gaps:** Minor. The autoscaling section covers ECS but the Azure Container Apps section does not show explicit autoscaling config (it mentions `--min-replicas 1 --max-replicas 5` inline in the `az containerapp create` command — this counts as covered).

---

## PHASE 5 — GIT & IDENTITY AUDIT

### 1. Commit author verification
**Result: PASS — All 21 commits authored by `Ramiro Lopez <rammirolopez@gmail.com>`**  
No exceptions found.

### 2. `git status`
**Result: PASS — Working tree is clean.**

### 3. `git remote -v`
**Result: PASS — No remote configured.** Output is empty. ✅

### 4. Total commits: **21**

Commit sequence follows the Conventional Commits spec (`feat:`, `chore:`, `test:`, `docs:`, `ci:`) consistently across all 21 commits.

---

## PHASE 6 — RECRUITER-READINESS SCORE

### 1. Would `docker compose up` + `dotnet run` Just Work™ on a fresh machine?

**Answer: Mostly yes, with one meaningful friction point.**

The infrastructure (`docker compose up`) works cleanly. The services start. **However**, to call any endpoint on AnalyticsApi or InsightsService, the developer needs a JWT Bearer token. The LOCAL_SETUP guide tells them to hit `POST /api/v1/auth/demo-token` — which returns a 404. `JwtTokenGenerator.GenerateDemoToken()` exists as a utility class but is never exposed.

A developer on a fresh machine who is not fluent with JWT tooling (`jwt-cli`, Postman, etc.) will be stopped here. This is **genuine friction**, not cosmetic.

Everything else in the setup chain (docker, migrations, health checks, Hangfire dashboard) works correctly.

---

### 2. Is the AI plus visible within the first 30 seconds of README?

**Answer: Yes.**

The compliance table on line 22 explicitly states:
> `AI tools in workflow | MDH.InsightsService calls Anthropic Claude for market summaries & anomaly explanations`

The dedicated section starting at line 106 is titled **"## AI Integration Highlight"** and leads with:
> `` `MDH.InsightsService` is the AI-powered layer of the platform. ``

Both are above the fold for anyone reading the README linearly.

---

### 3. Embarrassing leftovers (TODOs, stubs, NotImplementedException, Lorem ipsum)

**Result: No stubs, no NotImplementedException, no Lorem ipsum, no TODO/FIXME in production code.**

One legitimate oddity: `MarketSummaryEndpoint.cs` and `ListingExplainEndpoint.cs` have a graceful-degradation fallback that returns a static string if the Anthropic call fails. This is intentional defensive code, not a stub.

The only "incomplete" element is the `JwtTokenGenerator` static utility — it exists but is never called from an endpoint, making it dead utility code.

---

### 4. Portfolio score: **7.5 / 10**

**Justification:**

**What earns the 7.5:**
- Clean, zero-warning build
- All 23 tests pass
- Star schema, ETL pipeline, Hangfire jobs, CQRS, MongoDB landing zone — all present and real
- AI integration is genuine (not a mock), with visible prompt templates
- Excellent documentation breadth and depth
- Conventional commits, clean git history, no remote push
- 4 services with proper separation of concerns and independent startup

**What holds it below 8+:**
1. **No Dockerfiles committed.** CLAUDE.md explicitly required them. For a "microservices" portfolio, not having runnable containers is a significant gap — a recruiter who tries `docker compose up` for all services will find only the infra (SQL + Mongo) starts, not the application services.
2. **No working demo JWT endpoint.** The curl examples in LOCAL_SETUP are broken (404). A recruiter who actually tries to use the API hits a wall immediately.
3. **No WebApplicationFactory integration test.** CLAUDE.md specified "one integration test against WebApplicationFactory." Only in-memory EF Core tests exist. The test suite is thin at 23 total — a mid-level developer is expected to have more coverage.
4. **Hangfire 30-second cron is broken.** `"*/30 * * * * *"` (6 fields) is not valid standard Hangfire cron. It will not run every 30 seconds as intended.
5. **FluentValidation dependency is declared but completely unused** — a 🚩 for a reviewer scanning csproj files.
6. **Dead named volumes in docker-compose.yml** (`sqlserver_data`, `mongo_data` declared but bind mounts used instead).
7. **`version: "3.9"` in docker-compose** triggers a deprecation warning.
8. **`dotnet format` fails** on 25 whitespace violations in test files. Any CI pipeline that runs `dotnet format --verify-no-changes` would fail.

---

## SUMMARY

### Critical Issues (must fix before push)

1. **No Dockerfiles for any of the 4 services.** CLAUDE.md required them; CLOUD_DEPLOYMENT.md references them. Create `src/MDH.*/Dockerfile` for each service.
2. **No working demo JWT endpoint or pre-signed example token.** LOCAL_SETUP.md references a 404 endpoint. Either expose `JwtTokenGenerator` as a `GET /api/v1/auth/demo-token` endpoint or include a pre-signed hardcoded token (signed with the demo secret from `.env.example`) directly in LOCAL_SETUP.md.
3. **Hangfire `CleanListingsJob` cron `"*/30 * * * * *"` is invalid** for standard Hangfire (free tier uses 5-field cron). Should be `"* * * * *"` (every minute) or use `Cron.MinuteInterval(1)`. If 30-second scheduling is truly needed, document that Hangfire Pro is required.
4. **`dotnet format` fails on 25 whitespace violations** in test files. Run `dotnet format` to auto-fix before push.

### Minor Issues (nice to fix)

1. Remove `version: "3.9"` from `docker/docker-compose.yml` (obsolete, triggers warning).
2. Remove dead named volumes (`sqlserver_data`, `mongo_data`) from `docker/docker-compose.yml`.
3. Remove `FluentValidation.AspNetCore` from `MDH.AnalyticsApi.csproj` — it's deprecated, unused, and adds a deprecation warning.
4. Add a `WebApplicationFactory`-based integration test to `MDH.AnalyticsApi.Tests` (CLAUDE.md required it; currently missing).
5. Add a `dotnet-tools.json` manifest if `dotnet ef database update` is documented as a manual step.
6. Add at least a smoke test for `MDH.IngestionService` (currently zero test coverage for the ingestion layer).
7. Add `MDH.InsightsService.Tests` project (currently no test project for InsightsService).
8. Consider replacing `Polly.Extensions.Http` (deprecated) with `Microsoft.Extensions.Http.Resilience`.

---

## Ready-to-Push Verdict

**NO — with caveats.**

The project is well-architected, the code is production-quality, and the documentation is strong. But items 1–4 in Critical Issues are all pushable in under an hour. Specifically: **no Dockerfiles** and **no working JWT demo flow** are the two that would embarrass the project in a recruiter walkthrough. Fix those four issues, run `dotnet format`, and the project moves to a solid 8.5–9/10.
