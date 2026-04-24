// ============================================================================
// MultifamilyDataHub — Azure Free-Tier Infrastructure
// Target: Azure Container Apps (consumption, scale-to-zero) + Azure SQL
//         (serverless, free offer) + Cosmos DB (MongoDB API, free tier)
// ============================================================================
targetScope = 'resourceGroup'

// ---------------------------------------------------------------------------
// Parameters
// ---------------------------------------------------------------------------

@description('Azure region for all resources. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('Short prefix used in all resource names (2–8 chars).')
param namePrefix string = 'mdh'

@description('SQL Server administrator login name.')
param sqlAdminLogin string

@description('SQL Server administrator password (min 8 chars, uppercase + digit + symbol).')
@secure()
param sqlAdminPassword string

@description('Anthropic API key injected into InsightsService only.')
@secure()
param anthropicApiKey string

@description('JWT HS256 signing secret — must be ≥ 64 characters.')
@secure()
param jwtSecret string

@description('Docker image tag to deploy (e.g. "latest" or a git SHA).')
param imageTag string = 'latest'

@description('Docker Hub username that owns the four service images.')
param dockerHubUsername string

// ---------------------------------------------------------------------------
// Variables
// ---------------------------------------------------------------------------

// Globally-unique suffix derived from resource-group ID (deterministic per RG)
var uniqueSuffix = substring(uniqueString(resourceGroup().id), 0, 8)

var logAnalyticsName  = 'law-${namePrefix}'
var containerEnvName  = 'cae-${namePrefix}'
var sqlServerName     = 'sql-${namePrefix}-${uniqueSuffix}'
var cosmosAccountName = 'cosmos-${namePrefix}-${uniqueSuffix}'

// SQL connection string — uses environment().suffixes.sqlServerHostname to avoid hardcoded URLs
// ConnectRetryCount=3;ConnectRetryInterval=10 handles Azure SQL free-tier auto-pause cold start
var sqlHostSuffix = environment().suffixes.sqlServerHostname  // e.g. ".database.windows.net"
var sqlConnectionString = 'Server=tcp:${sqlServerName}${sqlHostSuffix},1433;Initial Catalog=MDH;User ID=${sqlAdminLogin};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60;ConnectRetryCount=3;ConnectRetryInterval=10;'

// Cosmos DB primary key resolved via resource symbol reference (preferred over listKeys() call)
var cosmosConnectionString = 'mongodb://${cosmosAccountName}:${cosmosAccount.listKeys().primaryMasterKey}@${cosmosAccountName}.mongo.cosmos.azure.com:10255/?ssl=true&replicaSet=globaldb&retrywrites=false&maxIdleTimeMS=120000&appName=@${cosmosAccountName}@'

// Docker Hub image references (public — no registry credentials needed)
var imgIngestion     = 'docker.io/${dockerHubUsername}/mdh-ingestion:${imageTag}'
var imgOrchestration = 'docker.io/${dockerHubUsername}/mdh-orchestration:${imageTag}'
var imgAnalyticsApi  = 'docker.io/${dockerHubUsername}/mdh-analytics-api:${imageTag}'
var imgInsights      = 'docker.io/${dockerHubUsername}/mdh-insights:${imageTag}'

// ---------------------------------------------------------------------------
// 1. Log Analytics Workspace
// SKU: PerGB2018 (pay-as-you-go, ~$2.30/GB after 5 GB free/month)
// ---------------------------------------------------------------------------
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  tags: { project: 'MultifamilyDataHub' }
  properties: {
    sku: { name: 'PerGB2018' }           // cheapest tier; first 5 GB/month free
    retentionInDays: 30
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}

// ---------------------------------------------------------------------------
// 2. Container Apps Environment (consumption plan = scale-to-zero)
// Free grant: 180k vCPU-s/mo + 360k GiB-s/mo + 2M requests/mo
// ---------------------------------------------------------------------------
resource containerEnv 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: containerEnvName
  location: location
  tags: { project: 'MultifamilyDataHub' }
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
    // Consumption plan (workloadProfileName not set = default consumption)
  }
}

// ---------------------------------------------------------------------------
// 3. Azure SQL logical server + free-offer serverless database
// SKU: GP_S_Gen5 (General Purpose Serverless) — first DB on this subscription
//      is free up to 100k vCore-s/mo and 32 GB storage (12-month free offer)
// ---------------------------------------------------------------------------
resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
  name: sqlServerName
  location: location
  tags: { project: 'MultifamilyDataHub' }
  properties: {
    administratorLogin: sqlAdminLogin
    administratorLoginPassword: sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
  }
}

// Allow Azure services (Container Apps) to reach the SQL server
resource sqlFirewallAzureServices 'Microsoft.Sql/servers/firewallRules@2023-05-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: 'MDH'
  location: location
  tags: { project: 'MultifamilyDataHub' }
  // GP_S_Gen5_1: General Purpose Serverless, 1 vCore
  // useFreeLimit: true activates the Azure SQL free offer (12 months, 1 per subscription)
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    maxSizeBytes: 34359738368    // 32 GB — free-offer maximum
    autoPauseDelay: 60           // Auto-pause after 60 min idle (saves cost; cold-start ~30–60 s)
    minCapacity: any('0.5')      // GP_S_Gen5 min vCores; Bicep type says int but API accepts decimal string
    useFreeLimit: true           // Activates the Azure SQL free-offer tier
    freeLimitExhaustionBehavior: 'AutoPause'
    requestedBackupStorageRedundancy: 'Local'
  }
}

// ---------------------------------------------------------------------------
// 4. Azure Cosmos DB for MongoDB (free tier)
// Free tier: 1000 RU/s + 25 GB storage, 1 account per subscription
// API: MongoDB 4.2 compatible
// ---------------------------------------------------------------------------
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-02-15-preview' = {
  name: cosmosAccountName
  location: location
  kind: 'MongoDB'                  // MongoDB API kind
  tags: { project: 'MultifamilyDataHub' }
  properties: {
    enableFreeTier: true           // 1000 RU/s + 25 GB free — one account per subscription
    databaseAccountOfferType: 'Standard'
    apiProperties: { serverVersion: '4.2' }
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    capabilities: [
      { name: 'EnableMongo' }
      { name: 'EnableServerless' }  // Serverless scales to 0 — removes baseline cost
    ]
    backupPolicy: {
      type: 'Continuous'            // Continuous backup at no extra cost on free tier
      continuousModeProperties: { tier: 'Continuous7Days' }
    }
  }
}

// Note: enableFreeTier and EnableServerless are mutually exclusive in some regions.
// If deployment fails with a conflict, remove EnableServerless and set shared throughput to 400 RU/s.

resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/mongodbDatabases@2024-02-15-preview' = {
  parent: cosmosAccount
  name: 'mdh_raw'
  properties: {
    resource: { id: 'mdh_raw' }
    // Shared throughput — omit for serverless accounts (throughput is per-request)
  }
}

resource cosmosCollection 'Microsoft.DocumentDB/databaseAccounts/mongodbDatabases/collections@2024-02-15-preview' = {
  parent: cosmosDatabase
  name: 'listings_raw'
  properties: {
    resource: {
      id: 'listings_raw'
      indexes: [
        {
          key: { keys: ['_id'] }
        }
        {
          key: { keys: ['processed'] }
        }
        {
          key: { keys: ['external_id'] }
          options: { unique: false }
        }
      ]
    }
  }
}

// ---------------------------------------------------------------------------
// 5. Container Apps
// Secrets are stored at the Container App level (no Key Vault — saves cost).
// All apps default to minReplicas=0 (scale-to-zero).
// ---------------------------------------------------------------------------

// ── 5a. Ingestion ─────────────────────────────────────────────────────────
// No ingress — this is a background worker with no HTTP surface.
// 0.25 vCPU / 0.5 GiB; max 1 replica (one writer to Cosmos DB is sufficient).
resource caIngestion 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${namePrefix}-ingestion'
  location: location
  tags: { project: 'MultifamilyDataHub', tier: 'worker' }
  properties: {
    environmentId: containerEnv.id
    configuration: {
      secrets: [
        { name: 'mongo-connection-string', value: cosmosConnectionString }
      ]
      // No ingress block → worker profile, no public endpoint
    }
    template: {
      containers: [
        {
          name: 'ingestion'
          image: imgIngestion
          resources: { cpu: any('0.25'), memory: '0.5Gi' }  // fractional CPU; any() bypasses Bicep int type
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT',  value: 'Production' }
            { name: 'MONGO_CONNECTION_STRING',  secretRef: 'mongo-connection-string' }
            { name: 'Ingestion__IntervalSeconds', value: '60'   }
            { name: 'Ingestion__BatchSize',       value: '50'   }
          ]
        }
      ]
      scale: {
        minReplicas: 0   // scale-to-zero when no work
        maxReplicas: 1   // single writer is safe for Cosmos DB free tier RU budget
      }
    }
  }
}

// ── 5b. Orchestration ─────────────────────────────────────────────────────
// Internal ingress only (Hangfire dashboard accessible via exec session, not public).
// 0.5 vCPU / 1 GiB — Hangfire SQL Server storage needs a bit more headroom.
resource caOrchestration 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${namePrefix}-orchestration'
  location: location
  tags: { project: 'MultifamilyDataHub', tier: 'worker' }
  properties: {
    environmentId: containerEnv.id
    configuration: {
      secrets: [
        // sqlConnectionString / cosmosConnectionString are derived from @secure() params.
        // Bicep can't trace @secure() through variable interpolation — suppress linter warning.
        #disable-next-line use-secure-value-for-secure-inputs
        { name: 'sql-connection-string',   value: sqlConnectionString   }
        { name: 'mongo-connection-string', value: cosmosConnectionString }
      ]
      ingress: {
        external: false           // internal only — not exposed to internet
        targetPort: 5020
        transport: 'http'
      }
    }
    template: {
      containers: [
        {
          name: 'orchestration'
          image: imgOrchestration
          resources: { cpu: any('0.5'), memory: '1Gi' }   // 0.5 vCPU / 1 GiB — Hangfire needs headroom; any() for fractional CPU
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT',   value: 'Production' }
            { name: 'SQL_CONNECTION_STRING',     secretRef: 'sql-connection-string'   }
            { name: 'MONGO_CONNECTION_STRING',   secretRef: 'mongo-connection-string' }
          ]
        }
      ]
      scale: {
        minReplicas: 0   // accepts cold start; Hangfire re-registers jobs on startup
        maxReplicas: 1   // single Hangfire server avoids duplicate job execution
      }
    }
  }
}

// ── 5c. Analytics API ─────────────────────────────────────────────────────
// External ingress — the public-facing REST API.
// HTTP scale rule: add replica when concurrent HTTP requests exceed 50.
resource caAnalyticsApi 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${namePrefix}-analytics-api'
  location: location
  tags: { project: 'MultifamilyDataHub', tier: 'api' }
  properties: {
    environmentId: containerEnv.id
    configuration: {
      secrets: [
        #disable-next-line use-secure-value-for-secure-inputs
        { name: 'sql-connection-string', value: sqlConnectionString }
        { name: 'jwt-secret',            value: jwtSecret           }
      ]
      ingress: {
        external: true
        targetPort: 5030
        transport: 'http'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'analytics-api'
          image: imgAnalyticsApi
          resources: { cpu: any('0.25'), memory: '0.5Gi' } // fractional CPU; any() bypasses Bicep int type
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'SQL_CONNECTION_STRING',  secretRef: 'sql-connection-string' }
            { name: 'JWT_SECRET',             secretRef: 'jwt-secret'            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 2
        rules: [
          {
            name: 'http-scale'
            http: {
              metadata: {
                concurrentRequests: '50'    // add replica at 50 in-flight HTTP requests
              }
            }
          }
        ]
      }
    }
  }
}

// ── 5d. Insights Service ──────────────────────────────────────────────────
// External ingress — AI-powered endpoint (rate-limited by Anthropic quota anyway).
resource caInsights 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${namePrefix}-insights'
  location: location
  tags: { project: 'MultifamilyDataHub', tier: 'api' }
  properties: {
    environmentId: containerEnv.id
    configuration: {
      secrets: [
        { name: 'anthropic-api-key', value: anthropicApiKey }
        { name: 'jwt-secret',        value: jwtSecret       }
      ]
      ingress: {
        external: true
        targetPort: 5040
        transport: 'http'
        allowInsecure: false
      }
    }
    template: {
      containers: [
        {
          name: 'insights'
          image: imgInsights
          resources: { cpu: any('0.25'), memory: '0.5Gi' } // fractional CPU; any() bypasses Bicep int type
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production'                          }
            { name: 'Anthropic__ApiKey',       secretRef: 'anthropic-api-key'              }
            { name: 'JWT_SECRET',              secretRef: 'jwt-secret'                     }
            { name: 'AnalyticsApi__BaseUrl',   value: 'https://${caAnalyticsApi.properties.configuration.ingress.fqdn}' }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
      }
    }
  }
}

// ---------------------------------------------------------------------------
// Outputs
// ---------------------------------------------------------------------------

@description('Public URL of the Analytics API Container App')
output analyticsApiUrl string = 'https://${caAnalyticsApi.properties.configuration.ingress.fqdn}'

@description('Public URL of the Insights Service Container App')
output insightsUrl string = 'https://${caInsights.properties.configuration.ingress.fqdn}'

@description('Azure SQL connection string (sensitive — store in Key Vault in production)')
@secure()
output sqlConnectionStringOut string = sqlConnectionString

@description('Cosmos DB MongoDB connection string (sensitive)')
@secure()
output cosmosConnectionStringOut string = cosmosConnectionString
