# 14 — Cloud Fundamentals

> **Study time:** ~30 minutes
> **Prerequisites:** [`02_ARCHITECTURE.md`](./02_ARCHITECTURE.md)

## Why this matters

Cloud deployment is no longer an "ops" topic — it is a backend engineer responsibility. The Smart Apartment Data JD lists "cloud (AWS/Azure)" as a requirement, not a nice-to-have. Engineers who can reason about containers, probes, secrets management, and cost control deploy with confidence. Engineers who can't are blocked by "DevOps" tickets.

This doc teaches the concepts behind what you deployed in Azure. The specific Azure commands are in [`14a_AZURE_PRODUCTION_DEPLOYMENT.md`](./14a_AZURE_PRODUCTION_DEPLOYMENT.md). This doc is the "why" behind the "how."

By the end of this doc you will be able to: (1) place IaaS, PaaS, and SaaS on a spectrum and explain where Azure Container Apps sits; (2) explain liveness vs readiness vs startup probes and why mixing them caused a real production outage; (3) describe where secrets belong on the management spectrum and why env vars are the worst option that is still commonly used.

---

## IaaS vs PaaS vs SaaS

**IaaS (Infrastructure as a Service):** You manage the OS, runtime, and app. The cloud provides VMs, networking, and storage. Example: Azure Virtual Machines, AWS EC2. Full control; full responsibility for patching, scaling, and OS hardening.

**PaaS (Platform as a Service):** The cloud manages the OS and runtime. You deploy your code/container. Example: Azure App Service, AWS Elastic Beanstalk, Azure Container Apps. Less control; the platform handles OS patches, load balancing, auto-scaling, and (in some cases) container orchestration.

**SaaS (Software as a Service):** The cloud manages everything including the application. You configure it. Example: Azure SQL Database, Cosmos DB, Office 365, GitHub. No OS, no runtime, no application code to manage.

**Where Azure Container Apps sits:** PaaS for containers. You provide a container image; the platform runs it on a Kubernetes cluster it manages (you never see or access the underlying Kubernetes). You configure replicas, ingress, secrets, and health probes. The platform handles scheduling, node management, TLS termination, and DNS.

This is why Azure Container Apps is the right choice for MDH — it gives us Kubernetes capabilities (scale-to-zero, rolling updates, environment management) without requiring us to operate a Kubernetes cluster.

---

## Containers vs VMs

A **virtual machine** virtualizes the entire hardware stack including an OS kernel. Booting a VM takes 30–90 seconds. VM images are typically gigabytes. Resource isolation is strong (full kernel separation).

A **container** virtualizes only the user space. Containers on the same host share the Linux kernel. Container images are typically tens to hundreds of megabytes. Start time is milliseconds to seconds. Resource isolation is via Linux namespaces (process tree, network, filesystem mount points) and cgroups (CPU and memory limits).

**OCI (Open Container Initiative):** The standard for container image format and runtime. `docker build` produces an OCI image. Azure Container Registry, Docker Hub, and Amazon ECR all store OCI images. Any OCI runtime (Docker, containerd, podman) can run them.

**Why containers won for backend deploys:** Consistent environment ("works on my machine" problems disappear), fast startup (milliseconds not minutes), small image size, immutable deployments (roll back = pull previous image tag), and per-container resource quotas.

---

## Container Orchestration Landscape

| Platform | Abstraction level | Best for |
|---|---|---|
| Docker Compose | Single machine | Local development |
| Kubernetes | Cluster, full control | Large teams, complex workloads, custom networking |
| ECS Fargate (AWS) | Serverless containers | AWS-native, no K8s expertise |
| Azure Container Apps | Serverless containers on K8s | .NET on Azure, scale-to-zero, managed |
| Nomad | Cluster, multi-workload | Multi-cloud, HashiCorp ecosystem |

**Azure Container Apps** runs on Kubernetes internally but exposes a simpler surface: you define an app, not a Pod/Deployment/Service/Ingress. The managed Kubernetes layer handles the rest. Scale-to-zero means the app uses zero compute when no requests arrive — the free tier grants apply only to actual usage.

---

## The 12-Factor App

The 12-Factor methodology (12factor.net) defines best practices for cloud-native, portable software. This project respects most of them:

| Factor | What it means | MDH status |
|---|---|---|
| I. Codebase | One repo, many deploys | ✅ Single git repo |
| II. Dependencies | Explicit in manifest | ✅ `Directory.Packages.props`, `global.json` |
| III. Config | Store in environment, not code | ✅ `SQL_CONNECTION_STRING` as env var (partial: still has appsettings fallbacks) |
| IV. Backing services | Treat as attached resources | ✅ SQL and MongoDB via connection strings |
| V. Build / release / run | Strict stages | ✅ Docker multi-stage build; CI/CD pipeline |
| VI. Processes | Stateless, share-nothing | ✅ Services are stateless; state in SQL and MongoDB |
| VII. Port binding | Self-contained | ✅ Each service listens on a dedicated port |
| VIII. Concurrency | Scale via process model | ✅ Container Apps scales replicas |
| IX. Disposability | Fast startup, graceful shutdown | ✅ `CancellationToken` handled; BackgroundService stops cleanly |
| X. Dev/prod parity | Keep environments similar | ⚠️ Docker Compose local ≈ Container Apps, but free-tier limits differ |
| XI. Logs | Treat as event streams | ✅ Serilog to stdout, forwarded to Log Analytics |
| XII. Admin processes | One-off tasks in same environment | ⚠️ Migrations run on startup, not as separate admin task |

The two "partial" items (III, Config) and (XII, Admin processes) are documented technical debt — appropriate for a portfolio project, production gaps for a real deployment.

---

## Stateless vs Stateful Services

All four services in MDH are **stateless** — they hold no in-memory state that persists between requests or ticks. If you kill a container and restart it, it behaves identically. This is what enables horizontal scaling (add replicas, any replica can serve any request) and scale-to-zero (no warm state to lose when the replica count drops to 0).

State lives in:
- **SQL Server** — warehouse data, job history
- **MongoDB** — raw landing zone

Any service can be restarted, redeployed, or replaced without data loss. This is a fundamental cloud-native property.

---

## Liveness vs Readiness vs Startup Probes

These are the three types of health checks that container orchestrators use to manage container lifecycle.

**Liveness probe:** "Is the container alive?" If liveness fails, the orchestrator kills and restarts the container. The intent is to catch stuck processes (deadlock, infinite loop, OOM). Liveness must always return 200 if the process is running and responsive. It must NOT check external dependencies — if the database is down, the process is still alive; restarting it won't fix the database.

**Readiness probe:** "Is the container ready to serve traffic?" If readiness fails, the orchestrator removes the container from the load balancer rotation but does NOT restart it. The intent is to handle slow startup (database migration, cache warmup) and temporary unavailability (DB circuit breaker open). Readiness CAN check external dependencies.

**Startup probe:** "Has the container finished starting up?" Used to give long-starting containers extra time before liveness checks begin. Not used in MDH — our services start in under 10 seconds.

**The bug that caused the outage:** All three services had `/health` running ALL health checks including the SQL Server check. When Azure SQL auto-paused after 60 minutes, `/health` returned 503 on the next liveness check. Azure Container Apps killed the container. Restart loop: container starts → SQL still waking up → health check fails → killed again. The fix: `Predicate = _ => false` for liveness (process alive, no SQL check), `Predicate = check => check.Tags.Contains("ready")` for readiness (SQL check, used only for traffic routing).

---

## Secrets Management Spectrum

From worst to best:

**Environment variables (worst of the "acceptable" options):** Secrets visible in `ps -e` output, stored in CI system logs if printed, easy to accidentally commit. But: universally supported, simple to set in any platform.

**Container Apps secrets / Kubernetes secrets:** Stored in platform-managed encrypted storage. Injected as env vars at container start. Not visible in process listing. Still accessible to anyone with cluster-level access. This is what MDH uses in production.

**Azure Key Vault / AWS Secrets Manager (best):** Secrets stored in a dedicated secrets service with access logging, rotation support, and fine-grained RBAC. Applications authenticate to Key Vault via managed identity (no credential needed to fetch credentials — the Azure platform itself handles authentication). Secrets are never stored in the container or in environment variables on disk.

**Managed Identity** (the gold standard for Azure): The Azure VM / Container App / Function has a system-assigned identity. It can request tokens from Azure Active Directory to call Azure services (Key Vault, SQL, Cosmos) without any stored credentials. The secret is: there is no secret. This is the production upgrade path for MDH.

---

## Observability Triad

**Logs:** Time-series text output. What Serilog provides. Good for: root cause analysis, sequential event tracing. Bad for: aggregate analysis, missing structure when not using structured logging.

**Metrics:** Aggregate numeric measurements over time. CPU %, request rate, error rate, p99 latency. What Azure Monitor and Prometheus provide. Good for: dashboards, alerting. Bad for: root cause analysis (metrics tell you something is wrong, not why).

**Traces (distributed traces):** End-to-end request paths across service boundaries. A single trace ID follows a request from AnalyticsApi through InsightsService to Anthropic, with per-span timing. What OpenTelemetry provides. Good for: latency profiling in distributed systems, finding bottlenecks.

**MDH current state:** Logs only (Serilog → stdout → Log Analytics). No metrics instrumentation. No distributed traces. The production upgrade: add `builder.Services.AddOpenTelemetry().WithTracing(...)` and export to Azure Monitor Application Insights. Total code change: ~20 lines.

---

## Cost Control

**Scale-to-zero:** The most powerful cost lever. Replicas drop to 0 when no traffic. Container Apps consumption plan charges only for actual compute consumed (vCPU-seconds × price). At zero replicas, cost is zero.

**Free tiers:**
- Container Apps: 180,000 vCPU-seconds + 360,000 GiB-seconds + 2M requests per month per subscription
- Azure SQL free offer: 100,000 vCore-seconds + 32 GB (12-month offer, 1 per subscription)
- Cosmos DB free tier: 1000 RU/s + 25 GB (permanent, 1 per subscription)

**Budget alerts:** Set a $5 monthly budget alert in Azure Cost Management. This catches surprises (e.g., a job running in an infinite loop consuming compute) before the bill arrives. See `14a_AZURE_PRODUCTION_DEPLOYMENT.md` for the CLI command.

**The idle tax:** Resources that exist but are not used still cost money (storage, reserved capacity). Scale-to-zero eliminates the compute idle tax. Storage (SQL, Cosmos) is charged by data size, not by request count — a 32 GB SQL database with zero queries per month still costs for 32 GB of storage.

---

## Exercise

1. What is the difference between `kubectl rollout restart` (Kubernetes) and `az containerapp update --min-replicas 0 --max-replicas 0` followed by `--max-replicas 1` (Container Apps)? When would you use each?

2. The liveness probe for OrchestrationService uses `Predicate = _ => false`. The process is running but Hangfire's SQL Server storage is unreachable. Does the liveness probe return 200 or 503? Should it?

3. An engineer proposes storing `SQL_CONNECTION_STRING` directly in `appsettings.Production.json` committed to git. List three specific risks with this approach.

4. MDH has logs but no metrics. Define the three most important metrics you would add first (hint: RED method — Rate, Errors, Duration) and which .NET library you would use.

---

## Common mistakes

- **Using the liveness probe URL as the readiness probe.** Orchestrators check liveness and readiness separately. If you configure Azure Container Apps to use `/health` for readiness AND liveness, and `/health` runs SQL checks, a paused Azure SQL kills the container instead of just removing it from the load balancer.

- **Storing secrets in container images.** Any `ENV SECRET=value` in a Dockerfile layer is baked into the image and visible to anyone who pulls it. Secrets must be injected at runtime, not baked in.

- **Not setting resource limits on containers.** Without CPU and memory limits, a runaway container can starve other containers on the same host. Container Apps allows setting `--cpu 0.25 --memory 0.5Gi` per container.

- **Assuming scale-to-zero is transparent.** When a container scales from 0 to 1, it takes 5–30 seconds (image pull + process start). The first request after scale-up may time out or see high latency. For user-facing APIs, consider a minimum replica of 1 if cold-start latency is unacceptable.

- **Confusing IaaS and PaaS security models.** On IaaS (VMs), you patch the OS. On PaaS (Container Apps), the platform patches the OS and runtime — but you still patch your application code and container image.

---

## Interview Angle — Smart Apartment Data

1. **"What is Azure Container Apps and how does it compare to ECS or Kubernetes?"** — Container Apps is a PaaS layer on top of Kubernetes. You deploy container images and define scaling rules; the platform manages the K8s cluster. It is simpler than raw K8s (no Pod/Service/Ingress YAML) and more opinionated than ECS. Scale-to-zero is built in. Right choice for .NET backend services that need Kubernetes capabilities without K8s operations expertise.

2. **"Explain liveness vs readiness probes."** — Liveness: is the container alive? Failure triggers restart. Must not check external dependencies — process health only. Readiness: is the container ready for traffic? Failure removes it from the load balancer but does not restart it. Can check SQL, cache warmup, etc. Mixing them caused a production outage: SQL auto-paused → readiness check failed → liveness returned 503 (same endpoint) → container restarted in a loop.

3. **"Where should secrets live in a production Azure deployment?"** — In Azure Key Vault, accessed via managed identity. The application authenticates to Key Vault using the Azure-assigned identity — no stored credentials. Container Apps secrets (environment variables at runtime) are the acceptable middle ground. Environment variables in appsettings.json committed to git are unacceptable.

4. **"What is the 12-factor app?"** — A methodology for cloud-native software: config in env vars, stateless processes, explicit dependencies, treat logs as event streams, etc. MDH follows most factors. The gaps: config still has appsettings.json fallbacks (partial factor III), and migrations run on startup instead of as a separate admin process (factor XII).

5. **30-second talking point:** "The four services deploy as Azure Container Apps on a consumption plan with scale-to-zero. Free tier covers demo traffic at ~$0/month. The architecture is stateless — any replica can serve any request; state lives in SQL and Cosmos. Liveness probes return 200 always; readiness probes check SQL and are used only for traffic routing. Secrets are Container App secrets injected as env vars — the production upgrade would be managed identity + Key Vault."

6. **Job requirement proof:** "Cloud (AWS/Azure)" — Azure Container Apps deployment with Bicep, scale-to-zero, health probe configuration, secrets management, cost control, deployed and verified live.
