# Cloud Deployment Guide — MultifamilyDataHub

## Overview

This guide covers two deployment paths:
- **AWS (primary):** ECR → ECS Fargate → RDS SQL Server → DocumentDB → Secrets Manager → CloudWatch
- **Azure (alternative):** ACR → Azure Container Apps → Azure SQL → Cosmos DB → Key Vault

---

## AWS Path (Primary)

### Architecture

```
Internet ──▶ ALB ──▶ ECS Fargate (4 services)
                        │
                        ├── MDH.IngestionService  ──▶ Amazon DocumentDB (MongoDB API)
                        ├── MDH.OrchestrationService ──▶ Amazon RDS SQL Server Express
                        ├── MDH.AnalyticsApi      ──▶ Amazon RDS SQL Server Express
                        └── MDH.InsightsService   ──▶ MDH.AnalyticsApi (ECS internal)
                                                       Anthropic API (internet)

All services read secrets from AWS Secrets Manager
Logs → Amazon CloudWatch Logs
```

### Prerequisites

```bash
# Install AWS CLI v2 and authenticate
aws configure

# Variables
export AWS_REGION=us-east-1
export AWS_ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
export ECR_PREFIX=$AWS_ACCOUNT_ID.dkr.ecr.$AWS_REGION.amazonaws.com
```

### Step 1: Create ECR Repositories

```bash
for service in mdh-ingestion mdh-orchestration mdh-analytics-api mdh-insights; do
  aws ecr create-repository --repository-name $service --region $AWS_REGION
done
```

### Step 2: Build and Push Images

```bash
# Authenticate Docker to ECR
aws ecr get-login-password --region $AWS_REGION | \
  docker login --username AWS --password-stdin $ECR_PREFIX

# Build and push each service
for svc in IngestionService OrchestrationService AnalyticsApi InsightsService; do
  lc=$(echo $svc | tr '[:upper:]' '[:lower:]' | sed 's/service/-service/;s/api/-api/')
  docker build -f src/MDH.$svc/Dockerfile -t mdh-$lc src/MDH.$svc/
  docker tag mdh-$lc:latest $ECR_PREFIX/mdh-$lc:latest
  docker push $ECR_PREFIX/mdh-$lc:latest
done
```

### Step 3: Create Dockerfiles

Each service needs a Dockerfile. Example for `MDH.AnalyticsApi`:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 5030

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["src/MDH.AnalyticsApi/MDH.AnalyticsApi.csproj", "src/MDH.AnalyticsApi/"]
COPY ["src/MDH.Shared/MDH.Shared.csproj", "src/MDH.Shared/"]
COPY ["Directory.Packages.props", "Directory.Build.props", "./"]
RUN dotnet restore "src/MDH.AnalyticsApi/MDH.AnalyticsApi.csproj"
COPY . .
RUN dotnet publish "src/MDH.AnalyticsApi/MDH.AnalyticsApi.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MDH.AnalyticsApi.dll"]
```

### Step 4: Create Secrets in AWS Secrets Manager

```bash
aws secretsmanager create-secret \
  --name mdh/anthropic-api-key \
  --secret-string "sk-ant-your-real-key"

aws secretsmanager create-secret \
  --name mdh/jwt-secret \
  --secret-string "your-64-char-random-secret"

aws secretsmanager create-secret \
  --name mdh/sql-connection-string \
  --secret-string "Server=your-rds-host,1433;Database=MDH;..."

aws secretsmanager create-secret \
  --name mdh/mongo-connection-string \
  --secret-string "mongodb://user:pass@your-docdb-host:27017"
```

### Step 5: Register ECS Task Definitions

```bash
# Example for AnalyticsApi (adapt for each service)
aws ecs register-task-definition --cli-input-json '{
  "family": "mdh-analytics-api",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "512",
  "memory": "1024",
  "executionRoleArn": "arn:aws:iam::ACCOUNT:role/ecsTaskExecutionRole",
  "containerDefinitions": [{
    "name": "analytics-api",
    "image": "ACCOUNT.dkr.ecr.us-east-1.amazonaws.com/mdh-analytics-api:latest",
    "portMappings": [{"containerPort": 5030, "protocol": "tcp"}],
    "environment": [{"name": "ASPNETCORE_ENVIRONMENT", "value": "Production"}],
    "secrets": [
      {"name": "SQL_CONNECTION_STRING", "valueFrom": "arn:aws:secretsmanager:...:mdh/sql-connection-string"},
      {"name": "JWT_SECRET", "valueFrom": "arn:aws:secretsmanager:...:mdh/jwt-secret"}
    ],
    "logConfiguration": {
      "logDriver": "awslogs",
      "options": {
        "awslogs-group": "/ecs/mdh-analytics-api",
        "awslogs-region": "us-east-1",
        "awslogs-stream-prefix": "ecs"
      }
    }
  }]
}'
```

### Step 6: Create ECS Services

```bash
aws ecs create-service \
  --cluster mdh-cluster \
  --service-name mdh-analytics-api \
  --task-definition mdh-analytics-api \
  --desired-count 2 \
  --launch-type FARGATE \
  --network-configuration "awsvpcConfiguration={subnets=[subnet-xxx],securityGroups=[sg-xxx],assignPublicIp=ENABLED}" \
  --load-balancers "targetGroupArn=arn:aws:elasticloadbalancing:...,containerName=analytics-api,containerPort=5030"
```

### Health Check Routing (ALB)

Configure ALB listener rules:
- `/api/v1/*` → `mdh-analytics-api` target group
- `/insights/*` → `mdh-insights` target group
- `/health` → each service's own health check

### Minimum-Viable Autoscaling

```bash
aws application-autoscaling register-scalable-target \
  --service-namespace ecs \
  --resource-id service/mdh-cluster/mdh-analytics-api \
  --scalable-dimension ecs:service:DesiredCount \
  --min-capacity 1 \
  --max-capacity 5

aws application-autoscaling put-scaling-policy \
  --policy-name cpu-scaling \
  --service-namespace ecs \
  --resource-id service/mdh-cluster/mdh-analytics-api \
  --scalable-dimension ecs:service:DesiredCount \
  --policy-type TargetTrackingScaling \
  --target-tracking-scaling-policy-configuration \
    "TargetValue=70.0,PredefinedMetricSpecification={PredefinedMetricType=ECSServiceAverageCPUUtilization}"
```

### Cost Estimate (AWS, minimal config)

| Resource | Config | ~Monthly Cost |
|---|---|---|
| ECS Fargate (4 services × 0.25 vCPU / 0.5 GB) | On-demand | ~$30 |
| RDS SQL Server Express (db.t3.micro) | Single-AZ | ~$25 |
| Amazon DocumentDB (db.t3.medium, 1 node) | | ~$65 |
| ALB | 1 LCU | ~$20 |
| CloudWatch Logs | 5 GB/month | ~$3 |
| **Total** | | **~$145/month** |

---

## Azure Path (Alternative)

### Architecture

```
Internet ──▶ Azure Container Apps Ingress ──▶ 4 Container Apps
                                                  │
                                                  ├── MDH.IngestionService  ──▶ Cosmos DB (Mongo API)
                                                  ├── MDH.OrchestrationService ──▶ Azure SQL
                                                  ├── MDH.AnalyticsApi      ──▶ Azure SQL
                                                  └── MDH.InsightsService   ──▶ MDH.AnalyticsApi
                                                                                 Anthropic API

Secrets from Azure Key Vault
Logs → Azure Monitor / Log Analytics
```

### Step 1: Create Azure Container Registry and Build Images

```bash
az login
export RG=mdh-rg
export ACR=mdhregistry
export LOCATION=eastus

az group create --name $RG --location $LOCATION
az acr create --resource-group $RG --name $ACR --sku Basic

# Build and push (ACR build does it on Azure, no local Docker needed)
az acr build --registry $ACR \
  --image mdh-analytics-api:latest \
  --file src/MDH.AnalyticsApi/Dockerfile .
```

### Step 2: Store Secrets in Key Vault

```bash
az keyvault create --name mdh-keyvault --resource-group $RG --location $LOCATION

az keyvault secret set --vault-name mdh-keyvault \
  --name anthropic-api-key --value "sk-ant-your-key"

az keyvault secret set --vault-name mdh-keyvault \
  --name jwt-secret --value "your-64-char-secret"
```

### Step 3: Create Container Apps Environment

```bash
az containerapp env create \
  --name mdh-env \
  --resource-group $RG \
  --location $LOCATION
```

### Step 4: Deploy Container Apps

```bash
az containerapp create \
  --name mdh-analytics-api \
  --resource-group $RG \
  --environment mdh-env \
  --image $ACR.azurecr.io/mdh-analytics-api:latest \
  --target-port 5030 \
  --ingress external \
  --min-replicas 1 \
  --max-replicas 5 \
  --cpu 0.25 \
  --memory 0.5Gi \
  --env-vars \
    "SQL_CONNECTION_STRING=secretref:sql-connection-string" \
    "JWT_SECRET=secretref:jwt-secret"
```

### Cost Estimate (Azure, minimal config)

| Resource | Config | ~Monthly Cost |
|---|---|---|
| Azure Container Apps (4 apps, vCPU-seconds) | ~0.5M vCPU-s/month | ~$20 |
| Azure SQL (Basic tier) | 5 DTUs | ~$5 |
| Cosmos DB (Mongo API, 400 RU/s) | Serverless | ~$10 |
| Azure Container Registry | Basic | ~$5 |
| Log Analytics | 5 GB/month | ~$6 |
| **Total** | | **~$46/month** |

> Azure Container Apps' consumption-based billing makes it significantly cheaper for low-traffic dev environments.
