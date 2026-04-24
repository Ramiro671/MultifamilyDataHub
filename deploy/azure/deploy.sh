#!/usr/bin/env bash
# ===========================================================================
# MultifamilyDataHub — Azure free-tier deployment (bash version)
# Idempotent: re-running converges the environment rather than failing.
# Requires: Azure CLI, Docker (running), git, jq
# ===========================================================================

set -euo pipefail

DOCKER_HUB_USERNAME="${1:-}"
SUBSCRIPTION_ID="${2:-}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-mdh}"
LOCATION="${LOCATION:-eastus}"
PARAMETERS_FILE="${PARAMETERS_FILE:-infra/main.parameters.json}"

# Change to repo root (script lives in deploy/azure/)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
cd "$REPO_ROOT"

step() { echo -e "\n\033[0;36m==> $1\033[0m"; }
err()  { echo -e "\033[0;31mERROR: $1\033[0m" >&2; exit 1; }

[[ -z "$DOCKER_HUB_USERNAME" ]] && err "Usage: $0 <DockerHubUsername> [SubscriptionId]"

# ---------------------------------------------------------------------------
# Step 1 — Azure login
# ---------------------------------------------------------------------------
step "Checking Azure login"
if ! az account show &>/dev/null; then
  echo "Not logged in — starting device-code login..."
  az login --use-device-code
fi
echo "Logged in: $(az account show --query 'user.name' -o tsv) / $(az account show --query 'name' -o tsv)"

# ---------------------------------------------------------------------------
# Step 2 — Select subscription
# ---------------------------------------------------------------------------
step "Selecting subscription"
if [[ -z "$SUBSCRIPTION_ID" ]]; then
  SUBSCRIPTION_COUNT=$(az account list --query 'length(@)' -o tsv)
  if [[ "$SUBSCRIPTION_COUNT" -eq 1 ]]; then
    SUBSCRIPTION_ID=$(az account show --query id -o tsv)
    echo "Only one subscription — using: $SUBSCRIPTION_ID"
  else
    echo "Available subscriptions:"
    az account list --query '[].{ID:id, Name:name}' -o table
    read -rp "Enter subscription ID: " SUBSCRIPTION_ID
  fi
fi
az account set --subscription "$SUBSCRIPTION_ID"
echo "Active subscription: $SUBSCRIPTION_ID"

# ---------------------------------------------------------------------------
# Step 3 — Resource group (idempotent)
# ---------------------------------------------------------------------------
step "Ensuring resource group '$RESOURCE_GROUP' exists in '$LOCATION'"
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none
echo "Resource group ready."

step "Registering resource providers"
for provider in Microsoft.App Microsoft.Sql Microsoft.DocumentDB Microsoft.OperationalInsights Microsoft.ContainerRegistry; do
  echo "  Registering $provider..."
  az provider register --namespace "$provider" --output none
done

# ---------------------------------------------------------------------------
# Step 4 — Build and push Docker images
# ---------------------------------------------------------------------------
step "Building and pushing Docker images to Docker Hub ($DOCKER_HUB_USERNAME)"

GIT_SHA=$(git rev-parse --short HEAD 2>/dev/null || echo "unknown")

declare -A DOCKERFILES
DOCKERFILES["mdh-ingestion"]="src/MDH.IngestionService/Dockerfile"
DOCKERFILES["mdh-orchestration"]="src/MDH.OrchestrationService/Dockerfile"
DOCKERFILES["mdh-analytics-api"]="src/MDH.AnalyticsApi/Dockerfile"
DOCKERFILES["mdh-insights"]="src/MDH.InsightsService/Dockerfile"

for svc in "${!DOCKERFILES[@]}"; do
  dockerfile="${DOCKERFILES[$svc]}"
  echo ""
  echo "  Building $svc..."
  docker build -f "$dockerfile" -t "$svc:build" .
  docker tag "$svc:build" "$DOCKER_HUB_USERNAME/$svc:latest"
  docker tag "$svc:build" "$DOCKER_HUB_USERNAME/$svc:$GIT_SHA"
  echo "  Pushing $DOCKER_HUB_USERNAME/$svc:latest ..."
  docker push "$DOCKER_HUB_USERNAME/$svc:latest"
  echo "  Pushing $DOCKER_HUB_USERNAME/$svc:$GIT_SHA ..."
  docker push "$DOCKER_HUB_USERNAME/$svc:$GIT_SHA"
done

# ---------------------------------------------------------------------------
# Step 5 — Bicep deployment
# ---------------------------------------------------------------------------
step "Running Bicep deployment (idempotent)"

[[ ! -f "$PARAMETERS_FILE" ]] && err "Parameters file not found: $PARAMETERS_FILE\nCopy infra/main.parameters.example.json to $PARAMETERS_FILE and fill in values."

DEPLOY_OUTPUT=$(az deployment group create \
  --resource-group   "$RESOURCE_GROUP"         \
  --template-file    infra/main.bicep           \
  --parameters       "@$PARAMETERS_FILE"        \
  --parameters       "dockerHubUsername=$DOCKER_HUB_USERNAME" \
  --parameters       "imageTag=$GIT_SHA"        \
  --output           json)

ANALYTICS_URL=$(echo "$DEPLOY_OUTPUT" | jq -r '.properties.outputs.analyticsApiUrl.value')
INSIGHTS_URL=$(echo "$DEPLOY_OUTPUT"  | jq -r '.properties.outputs.insightsUrl.value')

# ---------------------------------------------------------------------------
# Step 6 — Migrations note
# ---------------------------------------------------------------------------
step "Database migrations"
echo "  OrchestrationService applies EF Core migrations automatically on startup."
echo "  No manual migration step required — the container handles it with cold-start retry."

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
echo ""
echo "======================================================================"
echo "  Deployment complete!"
echo "======================================================================"
echo ""
echo "  Analytics API Swagger : ${ANALYTICS_URL}/swagger"
echo "  Insights Service      : ${INSIGHTS_URL}/swagger"
echo "  Analytics API health  : ${ANALYTICS_URL}/health"
echo "  Git SHA deployed      : ${GIT_SHA}"
echo ""
echo "  To tear down:"
echo "    az group delete --name $RESOURCE_GROUP --yes"
echo ""
