<#
.SYNOPSIS
    Deploys MultifamilyDataHub to Azure (free-tier Container Apps stack).

.DESCRIPTION
    Idempotent deployment script. Re-running converges the environment rather than failing.
    Requires: Azure CLI, Docker Desktop (running), git.

.PARAMETER DockerHubUsername
    Docker Hub username that will own the published images (e.g. "ramirolopez").

.PARAMETER SubscriptionId
    Azure subscription ID. If omitted, the script prompts interactively.

.PARAMETER ResourceGroup
    Azure resource group name. Default: "rg-mdh".

.PARAMETER Location
    Azure region. Default: "eastus".

.PARAMETER ParametersFile
    Path to the Bicep parameters file. Default: "infra/main.parameters.json".

.EXAMPLE
    .\deploy\azure\deploy.ps1 -DockerHubUsername myuser
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DockerHubUsername,

    [string]$SubscriptionId = "",
    [string]$ResourceGroup  = "rg-mdh",
    [string]$Location       = "eastus",
    [string]$ParametersFile = "infra/main.parameters.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $RepoRoot

function Write-Step([string]$msg) {
    Write-Host "`n==> $msg" -ForegroundColor Cyan
}

function Assert-ExitCode([int]$code, [string]$context) {
    if ($code -ne 0) { throw "Command failed ($context) with exit code $code" }
}

# ---------------------------------------------------------------------------
# Step 1 — Azure login
# ---------------------------------------------------------------------------
Write-Step "Checking Azure login"
$accountJson = az account show 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Not logged in — starting device-code login..."
    az login --use-device-code
    Assert-ExitCode $LASTEXITCODE "az login"
} else {
    $account = $accountJson | ConvertFrom-Json
    Write-Host "Already logged in as: $($account.user.name) ($($account.name))"
}

# ---------------------------------------------------------------------------
# Step 2 — Select subscription
# ---------------------------------------------------------------------------
Write-Step "Selecting subscription"
if ([string]::IsNullOrWhiteSpace($SubscriptionId)) {
    $subs = az account list --output json | ConvertFrom-Json
    if ($subs.Count -eq 1) {
        $SubscriptionId = $subs[0].id
        Write-Host "Only one subscription found — using: $($subs[0].name) ($SubscriptionId)"
    } else {
        Write-Host "Available subscriptions:"
        $subs | ForEach-Object { Write-Host "  [$($_.id)] $($_.name)" }
        $SubscriptionId = Read-Host "Enter subscription ID"
    }
}
az account set --subscription $SubscriptionId
Assert-ExitCode $LASTEXITCODE "az account set"
Write-Host "Active subscription: $SubscriptionId"

# ---------------------------------------------------------------------------
# Step 3 — Create resource group (idempotent)
# ---------------------------------------------------------------------------
Write-Step "Ensuring resource group '$ResourceGroup' exists in '$Location'"
az group create --name $ResourceGroup --location $Location --output none
Assert-ExitCode $LASTEXITCODE "az group create"
Write-Host "Resource group ready."

# Register required resource providers (idempotent — safe to re-run)
Write-Step "Registering resource providers"
$providers = @(
    "Microsoft.App",
    "Microsoft.Sql",
    "Microsoft.DocumentDB",
    "Microsoft.OperationalInsights",
    "Microsoft.ContainerRegistry"
)
foreach ($p in $providers) {
    Write-Host "  Registering $p..."
    az provider register --namespace $p --output none
}

# ---------------------------------------------------------------------------
# Step 4 — Build and push Docker images to Docker Hub
# ---------------------------------------------------------------------------
Write-Step "Building and pushing Docker images to Docker Hub ($DockerHubUsername)"

$GitSha = git rev-parse --short HEAD 2>$null
if ([string]::IsNullOrWhiteSpace($GitSha)) { $GitSha = "unknown" }

$services = @(
    @{ Name = "mdh-ingestion";     Context = "src/MDH.IngestionService";     Dockerfile = "src/MDH.IngestionService/Dockerfile" },
    @{ Name = "mdh-orchestration"; Context = ".";                            Dockerfile = "src/MDH.OrchestrationService/Dockerfile" },
    @{ Name = "mdh-analytics-api"; Context = ".";                            Dockerfile = "src/MDH.AnalyticsApi/Dockerfile" },
    @{ Name = "mdh-insights";      Context = ".";                            Dockerfile = "src/MDH.InsightsService/Dockerfile" }
)

# Dockerfiles use repo root as context (they COPY Directory.*.props from root)
foreach ($svc in $services) {
    $localTag  = "$($svc.Name):build"
    $hubLatest = "$DockerHubUsername/$($svc.Name):latest"
    $hubSha    = "$DockerHubUsername/$($svc.Name):$GitSha"

    Write-Host "`n  Building $($svc.Name)..."
    docker build -f $svc.Dockerfile -t $localTag .
    Assert-ExitCode $LASTEXITCODE "docker build $($svc.Name)"

    docker tag $localTag $hubLatest
    docker tag $localTag $hubSha

    Write-Host "  Pushing $hubLatest ..."
    docker push $hubLatest
    Assert-ExitCode $LASTEXITCODE "docker push $hubLatest"

    Write-Host "  Pushing $hubSha ..."
    docker push $hubSha
    Assert-ExitCode $LASTEXITCODE "docker push $hubSha"
}

# ---------------------------------------------------------------------------
# Step 5 — Bicep deployment
# ---------------------------------------------------------------------------
Write-Step "Running Bicep deployment (idempotent)"

if (-not (Test-Path $ParametersFile)) {
    throw "Parameters file not found: $ParametersFile`nCopy infra/main.parameters.example.json to $ParametersFile and fill in values."
}

$deployOutput = az deployment group create `
    --resource-group   $ResourceGroup `
    --template-file    infra/main.bicep `
    --parameters       "@$ParametersFile" `
    --parameters       dockerHubUsername=$DockerHubUsername `
    --parameters       imageTag=$GitSha `
    --output           json 2>&1

Assert-ExitCode $LASTEXITCODE "az deployment group create"

$deploy = $deployOutput | ConvertFrom-Json
$analyticsUrl = $deploy.properties.outputs.analyticsApiUrl.value
$insightsUrl  = $deploy.properties.outputs.insightsUrl.value

# ---------------------------------------------------------------------------
# Step 6 — Apply EF Core migrations via sqlcmd / az sql
# ---------------------------------------------------------------------------
Write-Step "Applying database migrations"
Write-Host @"
  NOTE: The OrchestrationService applies EF Core migrations automatically on startup.
  If you want to pre-run migrations manually, use the Azure SQL query editor in the portal
  or connect via: sqlcmd -S <server>.database.windows.net -d MDH -U <login> -P <password>

  The OrchestrationService will run migrations on first container startup (with retry for cold-start).
"@

# ---------------------------------------------------------------------------
# Done — print URLs
# ---------------------------------------------------------------------------
Write-Host "`n" + ("=" * 70) -ForegroundColor Green
Write-Host "  Deployment complete!" -ForegroundColor Green
Write-Host ("=" * 70) -ForegroundColor Green
Write-Host ""
Write-Host "  Analytics API Swagger : $analyticsUrl/swagger"      -ForegroundColor Yellow
Write-Host "  Insights Service      : $insightsUrl/swagger"       -ForegroundColor Yellow
Write-Host "  Analytics API health  : $analyticsUrl/health"       -ForegroundColor Yellow
Write-Host "  Git SHA deployed      : $GitSha"
Write-Host ""
Write-Host "  To scale everything down to zero replicas (free idle):"
Write-Host "    .\deploy\azure\deploy.ps1 -DockerHubUsername $DockerHubUsername (sets min=0 via Bicep)"
Write-Host ""
Write-Host "  To tear down:"
Write-Host "    az group delete --name $ResourceGroup --yes"
Write-Host ""

Pop-Location
