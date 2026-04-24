# Local Setup Guide — MultifamilyDataHub

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET 9 SDK | 9.0.x | [Download](https://dotnet.microsoft.com/download/dotnet/9) |
| Docker Desktop | 4.x+ | Required for SQL Server + MongoDB |
| Git | 2.x+ | |
| Visual Studio 2022 | 17.12+ | Optional — VS Code with C# Dev Kit also works |
| VS Code | Latest | Optional alternative to Visual Studio |

---

## Step-by-Step Setup

### 1. Clone the repository

```bash
git clone https://github.com/your-username/MultifamilyDataHub.git
cd MultifamilyDataHub
```

### 2. Configure environment

```bash
cp .env.example .env
```

Open `.env` and fill in:

```env
ANTHROPIC_API_KEY=sk-ant-your-real-key-here
SQL_CONNECTION_STRING=Server=localhost,1433;Database=MDH;User Id=sa;Password=Your_Strong_P@ss;TrustServerCertificate=True;
MONGO_CONNECTION_STRING=mongodb://mdh:mdh@localhost:27017
MONGO_DB=mdh_raw
JWT_SECRET=replace-with-64-char-random-string-here-minimum-64-bytes-pls
```

> **Note:** The `ANTHROPIC_API_KEY` is only needed for `MDH.InsightsService`. All other services run fine without it.

### 3. Start infrastructure

```bash
docker compose -f docker/docker-compose.yml up -d
```

Wait for containers to be healthy (~30s):

```bash
docker compose -f docker/docker-compose.yml ps
```

### 4. Apply EF Core migrations

The OrchestrationService applies migrations automatically on startup. To run manually:

```bash
dotnet ef database update --project src/MDH.OrchestrationService
```

> The `warehouse` and `hangfire` schemas are created automatically.

### 5. Run all four services in separate terminals

**Terminal 1 — Ingestion:**
```bash
dotnet run --project src/MDH.IngestionService
# Health: http://localhost:5010/health
```

**Terminal 2 — Orchestration:**
```bash
dotnet run --project src/MDH.OrchestrationService
# Hangfire dashboard: http://localhost:5020/hangfire
# Health: http://localhost:5020/health
```

**Terminal 3 — Analytics API:**
```bash
dotnet run --project src/MDH.AnalyticsApi
# Swagger: http://localhost:5030/swagger
# Health: http://localhost:5030/health
```

**Terminal 4 — Insights Service:**
```bash
dotnet run --project src/MDH.InsightsService
# Swagger: http://localhost:5040/swagger
# Health: http://localhost:5040/health
```

---

## Attaching the Debugger

### Visual Studio 2022

1. Build the solution (`Ctrl+Shift+B`)
2. Run the service you want to debug from a terminal (step 5 above)
3. Go to **Debug → Attach to Process** (`Ctrl+Alt+P`)
4. Find `dotnet.exe` processes and pick the one for your service
5. Set breakpoints at any of the `🔴 BREAKPOINT` comments listed below

### VS Code (`launch.json`)

Add to `.vscode/launch.json`:

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Attach to IngestionService",
      "type": "coreclr",
      "request": "attach",
      "processName": "MDH.IngestionService"
    },
    {
      "name": "Attach to AnalyticsApi",
      "type": "coreclr",
      "request": "attach",
      "processName": "MDH.AnalyticsApi"
    },
    {
      "name": "Launch AnalyticsApi",
      "type": "coreclr",
      "request": "launch",
      "program": "${workspaceFolder}/src/MDH.AnalyticsApi/bin/Debug/net9.0/MDH.AnalyticsApi.dll",
      "cwd": "${workspaceFolder}/src/MDH.AnalyticsApi",
      "env": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  ]
}
```

---

## Breakpoint Map

Jump directly to the most instructive code paths:

| File | Comment | What happens here |
|---|---|---|
| `src/MDH.IngestionService/Persistence/RawListingStore.cs` | `🔴 BREAKPOINT: raw batch persisted` | After 1000 docs written to MongoDB; inspect `listings` variable |
| `src/MDH.OrchestrationService/Jobs/CleanListingsJob.cs` | `🔴 BREAKPOINT: starting ETL batch` | ETL loop start; inspect raw listing count and submarket map |
| `src/MDH.OrchestrationService/Jobs/BuildMarketMetricsJob.cs` | `🔴 BREAKPOINT: starting ETL batch` | Aggregation start; set watch on `rentData.Count` |
| `src/MDH.OrchestrationService/Jobs/DetectAnomaliesJob.cs` | `🔴 BREAKPOINT: starting ETL batch` | Anomaly scan start; watch `mean`, `stdDev`, `zScore` per group |
| `src/MDH.AnalyticsApi/Features/Markets/GetMarketsQuery.cs` | `🔴 BREAKPOINT: query received` | Before DB query; inspect incoming request parameters |
| `src/MDH.AnalyticsApi/Features/Listings/SearchListingsQuery.cs` | `🔴 BREAKPOINT: query received` | Paginated search handler entry |
| `src/MDH.InsightsService/Anthropic/AnthropicClient.cs` | `🔴 BREAKPOINT: prompt assembled, sending to Claude` | Just before HTTP call; inspect `systemPrompt` and `userMessage` |

---

## Using Swagger

1. Open `http://localhost:5030/swagger`
2. Click **Authorize** (lock icon) and enter: `Bearer <jwt>`
3. Generate a demo JWT (see below)
4. Try `GET /api/v1/markets`

---

## Generating a Demo JWT

Use this `curl` command to get a token from the demo endpoint (or generate one manually):

```bash
# Generate JWT manually using the demo secret
curl -X POST http://localhost:5030/api/v1/auth/demo-token 2>/dev/null || \
  echo "Use the JWT generator utility in MDH.AnalyticsApi.Infrastructure.JwtTokenGenerator"
```

Or use this PowerShell snippet with the default demo secret:

```powershell
# Using a pre-signed demo token (valid for 24h, HS256, secret: replace-with-64-char...)
# For real testing, use MDH.AnalyticsApi.Infrastructure.JwtTokenGenerator.GenerateDemoToken()
```

**Sample authenticated `curl` request:**

```bash
# Replace <JWT> with your token
curl -H "Authorization: Bearer <JWT>" \
     http://localhost:5030/api/v1/markets

curl -H "Authorization: Bearer <JWT>" \
     "http://localhost:5030/api/v1/listings/search?submarket=Austin&bedrooms=2&page=1&pageSize=10"
```

---

## Regenerating Synthetic Data

The IngestionService runs continuously. To regenerate data from scratch:

1. Stop all services
2. Clear MongoDB: `docker exec mdh-mongo mongosh -u mdh -p mdh --eval "use mdh_raw; db.listings_raw.drop()"`
3. Clear SQL Server warehouse tables (optional): run `TRUNCATE TABLE warehouse.fact_daily_rent; ...`
4. Restart all services — ingestion resumes immediately

---

## Useful URLs at a Glance

| Service | URL |
|---|---|
| Ingestion health | http://localhost:5010/health |
| Hangfire dashboard | http://localhost:5020/hangfire |
| Orchestration health | http://localhost:5020/health |
| Analytics API Swagger | http://localhost:5030/swagger |
| Insights Service Swagger | http://localhost:5040/swagger |
