# Azure Production Deployment — MultifamilyDataHub

> **This is the actionable deploy guide.** For the conceptual AWS vs Azure comparison see [`docs/CLOUD_DEPLOYMENT.md`](CLOUD_DEPLOYMENT.md).

---

## 1. Objective

Deploy all four MultifamilyDataHub services to Azure using the consumption-plan free tier so the running cost stays at **$0–$5/month** during demo/validation. The stack auto-pauses everything when idle and scales to zero between requests.

Target architecture:

```
Internet ──▶ Azure Container Apps (cae-mdh, consumption plan, scale-to-zero)
               ├── ca-mdh-analytics-api   (external ingress, port 5030)
               ├── ca-mdh-insights        (external ingress, port 5040)
               ├── ca-mdh-orchestration   (internal only, port 5020)
               └── ca-mdh-ingestion       (no ingress — background worker)
                        │
                        ├──▶ Azure SQL "MDH"     (GP_S_Gen5, serverless, auto-pause 60 min)
                        ├──▶ Cosmos DB "mdh_raw" (MongoDB API, free tier, 1000 RU/s)
                        └──▶ Anthropic API       (InsightsService only, outbound)
```

---

## 2. Prerequisites

| Tool | Version | Install |
|---|---|---|
| Azure CLI | 2.60+ | `winget install Microsoft.AzureCLI` / [aka.ms/installazurecliwindows](https://aka.ms/installazurecliwindows) |
| Bicep CLI | Latest | `az bicep install` (installs as az extension) |
| Docker Desktop | 4.x+ | Required for local image builds |
| Git | 2.x+ | Already present |
| PowerShell | 7.x+ (or 5.1) | Required for `deploy.ps1` |
| jq | Any | Required for `deploy.sh` (bash users) |
| Azure subscription | — | Free account works; **only one Cosmos DB free-tier account per subscription** |
| Docker Hub account | — | Free; images are public |
| Anthropic API key | — | From [console.anthropic.com](https://console.anthropic.com) |

---

## 3. Free-Tier Budget Math

### Azure Container Apps free grant (per subscription per month)

| Metric | Free grant | This demo usage | Headroom |
|---|---|---|---|
| vCPU-seconds | 180,000 | ~8,640 (4 apps × 0.25 vCPU × 1 hr/day active) | **95% free** |
| GiB-seconds | 360,000 | ~17,280 (4 × 0.5 GiB × 1 hr/day) | **95% free** |
| Requests | 2,000,000 | Demo traffic (< 10k/day) | Well under |

### Azure SQL free offer (12 months per subscription)

- 1 free database per subscription for the first 12 months
- 100,000 vCore-seconds/month GP_S_Gen5 compute
- 32 GB storage included
- **Auto-pause after 60 minutes idle** → $0 when not in use
- Cold-start latency: 30–60 seconds (handled by retry logic in OrchestrationService and EF Core)

### Cosmos DB free tier (permanent, 1 per subscription)

- 1,000 RU/s throughput + 25 GB storage — **permanently free**
- This demo throttles IngestionService to `BatchSize=50, IntervalSeconds=60`
- 50 documents × ~8 RU/insert = ~400 RU/tick → well within 1000 RU/s budget
- If you see HTTP 429 errors, increase `IntervalSeconds` to 120

### Total estimated monthly cost (active demo)

| Resource | Cost |
|---|---|
| Container Apps (scale-to-zero, light traffic) | $0 (within free grant) |
| Azure SQL (serverless, auto-pause) | $0 (within free offer) |
| Cosmos DB (free tier) | $0 |
| Log Analytics (< 5 GB/mo) | $0 (5 GB/mo free) |
| Docker Hub (public images) | $0 |
| **Total** | **$0–$2/month** |

> After the 12-month SQL free offer expires, cost rises to ~$5–10/month for serverless compute.

---

## 4. One-Time Setup

### 4a. Login and register providers

```bash
az login
az account set --subscription <YOUR_SUBSCRIPTION_ID>

# Register resource providers (idempotent)
for p in Microsoft.App Microsoft.Sql Microsoft.DocumentDB \
          Microsoft.OperationalInsights Microsoft.ContainerRegistry; do
  az provider register --namespace "$p"
done

# Install Bicep
az bicep install
```

### 4b. Docker Hub login

```bash
docker login
# Enter your Docker Hub username and password/token
```

---

## 5. Fill `infra/main.parameters.json`

> **WARNING: Never commit `main.parameters.json` to git.** It contains real secrets. It is already in `.gitignore`.

```bash
# PowerShell
Copy-Item infra/main.parameters.example.json infra/main.parameters.json
notepad infra/main.parameters.json

# Bash
cp infra/main.parameters.example.json infra/main.parameters.json
nano infra/main.parameters.json
```

Fill in every `REPLACE_*` placeholder:

| Parameter | What to put |
|---|---|
| `sqlAdminLogin` | A username for the SQL admin (e.g. `mdhsqladmin`) |
| `sqlAdminPassword` | Strong password: min 8 chars, 1 uppercase, 1 digit, 1 symbol |
| `anthropicApiKey` | Your `sk-ant-...` key from console.anthropic.com |
| `jwtSecret` | A random 64-char string (run: `openssl rand -base64 48`) |
| `dockerHubUsername` | Your Docker Hub username |
| `imageTag` | `latest` (the deploy script overwrites this with the git SHA) |

Verify the file is gitignored:

```bash
git check-ignore infra/main.parameters.json
# Expected output: infra/main.parameters.json
```

---

## 6. Run `deploy/azure/deploy.ps1`

```powershell
# From the repo root
.\deploy\azure\deploy.ps1 -DockerHubUsername <your-dockerhub-username>
```

### Expected output at each phase

**Phase 1 — Azure login check:**
```
==> Checking Azure login
Already logged in as: you@example.com (My Subscription)
```

**Phase 2 — Subscription select:**
```
==> Selecting subscription
Active subscription: xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx
```

**Phase 3 — Resource group:**
```
==> Ensuring resource group 'rg-mdh' exists in 'eastus'
Resource group ready.
==> Registering resource providers
  Registering Microsoft.App...
  ...
```

**Phase 4 — Docker build and push (~5–10 min first run, cached after):**
```
==> Building and pushing Docker images to Docker Hub (youruser)

  Building mdh-ingestion...
  [+] Building 45.2s (12/12) FINISHED
  Pushing youruser/mdh-ingestion:latest ...
  ...
```

**Phase 5 — Bicep deployment (~3–5 min):**
```
==> Running Bicep deployment (idempotent)
```
The Bicep deployment creates (or updates) all resources. On first run this takes 3–5 minutes.

**Phase 6 — Summary:**
```
======================================================================
  Deployment complete!
======================================================================

  Analytics API Swagger : https://ca-mdh-analytics-api.happyfield-xxx.eastus.azurecontainerapps.io/swagger
  Insights Service      : https://ca-mdh-insights.happyfield-xxx.eastus.azurecontainerapps.io/swagger
  Git SHA deployed      : a1b2c3d
```

---

## 7. Post-Deployment Verification

### 7a. Health checks

```bash
# Replace URLs with your actual Container App FQDNs from deploy output
ANALYTICS_URL="https://ca-mdh-analytics-api.<env>.eastus.azurecontainerapps.io"
INSIGHTS_URL="https://ca-mdh-insights.<env>.eastus.azurecontainerapps.io"

curl $ANALYTICS_URL/health
# Expected: Healthy

curl $ANALYTICS_URL/health/ready
# Expected: Healthy (SQL must be reachable — may take 60 s on first cold start)

curl $INSIGHTS_URL/health
# Expected: Healthy
```

### 7b. Generate a JWT and call the Analytics API

```bash
# Get a demo token (the demo endpoint returns a signed token)
# If you haven't exposed /api/v1/auth/demo-token, generate one locally:
dotnet run --project src/MDH.AnalyticsApi -- --generate-token  # or use the JwtTokenGenerator utility

# Then call a protected endpoint
curl -H "Authorization: Bearer <JWT>" $ANALYTICS_URL/api/v1/markets
```

### 7c. Check Swagger

Open in browser:
- Analytics API: `https://ca-mdh-analytics-api.<env>/swagger`
- Insights Service: `https://ca-mdh-insights.<env>/swagger`

### 7d. Check Hangfire dashboard (internal — requires exec session)

The Orchestration service has internal ingress only. Access the Hangfire dashboard via Container Apps exec:

```bash
# Get the orchestration container name
az containerapp exec \
  --name ca-mdh-orchestration \
  --resource-group rg-mdh \
  --command "/bin/sh"

# From inside the container, curl the dashboard
curl http://localhost:5020/hangfire
```

Alternatively, use the Azure portal → Container Apps → ca-mdh-orchestration → Console.

### 7e. Check Cosmos DB Data Explorer

1. Azure portal → Cosmos DB → `cosmos-mdh-<suffix>` → Data Explorer
2. Expand `mdh_raw` → `listings_raw`
3. After ~2 minutes (first ingestion tick), you should see 50 documents with `processed: false`
4. After ~5 minutes, OrchestrationService's ETL job marks them `processed: true`

### 7f. Verify SQL warehouse tables

```bash
# Connect via Azure portal → SQL databases → MDH → Query editor
# Login with your SQL admin credentials, then:
SELECT TOP 5 * FROM warehouse.dim_submarket;
SELECT COUNT(*) FROM warehouse.fact_daily_rent;
```

---

## 8. Scale Down / Pause (Return to $0)

Container Apps scale to zero automatically when idle (this is the default — `minReplicas: 0`). To force immediate scale-down:

```bash
# Scale all Container Apps to 0 replicas immediately
for app in ca-mdh-ingestion ca-mdh-orchestration ca-mdh-analytics-api ca-mdh-insights; do
  az containerapp update \
    --name "$app" \
    --resource-group rg-mdh \
    --min-replicas 0 \
    --max-replicas 0 \
    --output none
  echo "Scaled $app to 0"
done
```

To resume, re-run the deploy script (sets min-replicas back to 0 / max as defined in Bicep — triggers scale-up on first request).

---

## 9. Tear Down

When the demo is complete, delete everything with one command:

```bash
az group delete --name rg-mdh --yes
```

This deletes all resources in the resource group (SQL, Cosmos DB, Container Apps, Log Analytics). There is no cost after deletion.

> **Note:** The Cosmos DB free-tier slot and SQL free-offer slot are **subscription-level** — they will be available again for future deployments.

---

## 10. Troubleshooting

### "Container App stuck at Provisioning"

```bash
# Check deployment events
az containerapp show \
  --name ca-mdh-analytics-api \
  --resource-group rg-mdh \
  --query "properties.provisioningState"

# Check revision status
az containerapp revision list \
  --name ca-mdh-analytics-api \
  --resource-group rg-mdh \
  --output table
```

Common causes: image not found on Docker Hub (check tag exists), or insufficient vCPU quota (request a quota increase in the portal).

### "SQL connection timeout on startup (cold start)"

Expected behavior — Azure SQL serverless free tier auto-pauses after 60 minutes idle. OrchestrationService's `MigrateWithRetryAsync` retries up to 6 times with exponential backoff (2, 4, 8, 16, 32 seconds). The container will start successfully within ~60 seconds.

If you see this repeatedly in production logs:
```
Migration attempt 1/6 failed (likely SQL cold-start). Retrying in 2s
Migration attempt 2/6 failed (likely SQL cold-start). Retrying in 4s
```
This is normal — wait for attempt 3–6 to succeed.

To reduce cold-start frequency, increase the auto-pause delay:
```bash
az sql db update \
  --server sql-mdh-<suffix> \
  --resource-group rg-mdh \
  --name MDH \
  --auto-pause-delay 120  # pause after 2 hours idle instead of 1
```

### "Cosmos DB 429 Too Many Requests (RU limit exceeded)"

The free tier is capped at 1000 RU/s. IngestionService writes 50 docs × ~8 RU = 400 RU/tick every 60 seconds — within budget. If you see 429s:

```bash
# Reduce ingestion rate via Container App env vars
az containerapp update \
  --name ca-mdh-ingestion \
  --resource-group rg-mdh \
  --set-env-vars "Ingestion__IntervalSeconds=120" "Ingestion__BatchSize=25"
```

Check RU consumption in the portal: Cosmos DB → Metrics → Total Request Units.

### "Anthropic 401 Unauthorized (secret not bound correctly)"

Verify the secret is set on the Container App:
```bash
az containerapp secret list \
  --name ca-mdh-insights \
  --resource-group rg-mdh
# Should show: anthropic-api-key

# Verify env var references the secret
az containerapp show \
  --name ca-mdh-insights \
  --resource-group rg-mdh \
  --query "properties.template.containers[0].env"
# Should show: {"name":"Anthropic__ApiKey","secretRef":"anthropic-api-key"}
```

If the secret is missing, re-run the deploy script — the Bicep will update the Container App with the correct secret binding.

### "InsightsService can't reach AnalyticsApi"

InsightsService reads `AnalyticsApi:BaseUrl` which is set to the AnalyticsApi Container App FQDN in `appsettings.Production.json` and overridden in the Bicep template. Verify:

```bash
az containerapp show \
  --name ca-mdh-insights \
  --resource-group rg-mdh \
  --query "properties.template.containers[0].env[?name=='AnalyticsApi__BaseUrl']"
```

The value should be `https://ca-mdh-analytics-api.<env>.eastus.azurecontainerapps.io`.

---

## 11. Cost Monitoring

### Set a $5 budget alert

```bash
az consumption budget create \
  --budget-name mdh-budget \
  --amount 5 \
  --time-grain Monthly \
  --start-date "$(date +%Y-%m-01)" \
  --end-date   "$(date -d '+2 years' +%Y-%m-01)" \
  --resource-group rg-mdh \
  --notifications \
    threshold=80,operator=GreaterThan,contact-emails=your@email.com,notification-enabled=true \
    threshold=100,operator=GreaterThan,contact-emails=your@email.com,notification-enabled=true
```

### Read Container Apps free-grant consumption

```bash
# In the portal: Monitor → Metrics → select the Container Apps Environment
# Metric: "Billable vCPU Seconds" / "Billable Memory GiB Seconds"
# Compare to free grant: 180k vCPU-s / 360k GiB-s per month

# Via CLI
az monitor metrics list \
  --resource $(az containerapp env show --name cae-mdh --resource-group rg-mdh --query id -o tsv) \
  --metric "BillableVCpuSeconds" \
  --start-time "$(date -d '-30 days' --iso-8601)" \
  --end-time "$(date --iso-8601)" \
  --aggregation Total \
  --output table
```

---

## GitHub Actions (CI/CD)

For automated deploys via GitHub Actions, see `.github/workflows/deploy-azure.yml`.

**Required GitHub secrets** (Settings → Secrets and variables → Actions):

| Secret | Value |
|---|---|
| `AZURE_CLIENT_ID` | App registration client ID (from OIDC setup) |
| `AZURE_TENANT_ID` | Azure AD tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID |
| `DOCKERHUB_USERNAME` | Docker Hub username |
| `DOCKERHUB_TOKEN` | Docker Hub access token |
| `SQL_ADMIN_LOGIN` | SQL admin username |
| `SQL_ADMIN_PASSWORD` | SQL admin password |
| `ANTHROPIC_API_KEY` | `sk-ant-...` key |
| `JWT_SECRET` | 64-char random string |

The workflow runs **only on manual dispatch** (Actions → Deploy to Azure → Run workflow).
