# 14b — DevOps and Software Supply Chain

> **Study time:** ~25 minutes
> **Prerequisites:** [`14_CLOUD_FUNDAMENTALS.md`](./14_CLOUD_FUNDAMENTALS.md), [`14a_AZURE_PRODUCTION_DEPLOYMENT.md`](./14a_AZURE_PRODUCTION_DEPLOYMENT.md)

## Why this matters

Modern backend engineers own their deployment pipeline. "CI/CD" and "Docker" are in every backend job description. If you cannot explain your Dockerfile layer-by-layer, describe what your CI pipeline does, or justify your image tagging strategy, you are handing easy points to competing candidates. This doc converts that gap into a strength.

By the end of this doc you will be able to: (1) walk through a multi-stage Dockerfile line-by-line and explain every instruction; (2) describe what the GitHub Actions CI pipeline does and why the test step comes after build; (3) explain image tagging discipline and the risk of `:latest`-only tagging.

---

## Dockerfile Anatomy — Line by Line

Full file: `src/MDH.IngestionService/Dockerfile`

```dockerfile
# syntax=docker/dockerfile:1
# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
```

`FROM ... AS build` names this stage "build". Multi-stage builds separate the compilation environment (SDK, full toolchain) from the runtime environment (ASP.NET runtime only). The final image does not contain the SDK — only the published output.

`mcr.microsoft.com/dotnet/sdk:9.0` is Microsoft's official SDK image. Using the MCR (Microsoft Container Registry) image is preferable to Docker Hub for .NET because Microsoft maintains it with OS security patches. The SDK image is ~800MB; the runtime image is ~200MB. The final container only uses the runtime layer.

```dockerfile
# Copy central NuGet and build props first so restore layer is cached
COPY NuGet.config Directory.Packages.props Directory.Build.props ./
```

**Layer caching strategy:** Docker builds each instruction as a layer. If a layer's inputs have not changed, Docker reuses the cached layer. Copying `*.props` files before source code means the NuGet restore layer is cached as long as package versions have not changed — even if source files changed. Without this, every code change would trigger a full `dotnet restore` (slow).

```dockerfile
COPY src/MDH.Shared/MDH.Shared.csproj          src/MDH.Shared/
COPY src/MDH.IngestionService/MDH.IngestionService.csproj  src/MDH.IngestionService/
RUN dotnet restore src/MDH.IngestionService/MDH.IngestionService.csproj
```

Copy `.csproj` files only (not source), restore. This layer is cached until project file dependencies change. A source code change does not invalidate this layer — critical for fast CI builds.

```dockerfile
COPY src/MDH.Shared/            src/MDH.Shared/
COPY src/MDH.IngestionService/  src/MDH.IngestionService/
RUN dotnet publish src/MDH.IngestionService/MDH.IngestionService.csproj \
    -c Release -o /app/publish --no-restore
```

Copy full source and publish. `--no-restore` skips restoration since the previous layer already restored. `dotnet publish` compiles and produces self-contained output in `/app/publish`.

```dockerfile
# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Non-root user for security
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .
EXPOSE 5010
ENTRYPOINT ["dotnet", "MDH.IngestionService.dll"]
```

`FROM mcr.microsoft.com/dotnet/aspnet:9.0` — the runtime-only image (~200MB vs ~800MB SDK). Only the published DLLs are copied from the build stage.

**Non-root user:** Creating `appuser` and switching to it before running the app prevents the container process from running as root. If the app is compromised, an attacker has limited host privileges (namespaced to the container, and even within the container, no root capabilities). This is a basic container security practice.

`EXPOSE 5010` declares the port for documentation and tooling. It does not actually publish the port — `docker run -p 5010:5010` or the Container App target port does that.

`ENTRYPOINT ["dotnet", "MDH.IngestionService.dll"]` uses the exec form (JSON array), not the shell form (`dotnet MDH...`). The exec form does not spawn a shell — the `dotnet` process is PID 1 in the container, receiving SIGTERM directly. Shell form (`CMD dotnet ...`) spawns `sh -c "dotnet ..."` as PID 1, and SIGTERM is sent to the shell, which may or may not forward it to the child process — graceful shutdown may fail.

---

## `.dockerignore`

The `.dockerignore` file prevents the Docker build context from including unnecessary files:

```
obj/
bin/
.git/
*.md
.env
infra/
```

Without `.dockerignore`, `COPY . .` sends the entire repo directory to the Docker daemon (including `obj/`, `bin/`, `.git/`, and any `.env` files). Problems:
- **Build context size:** `obj/` and `bin/` can be hundreds of MB — sending them to the daemon wastes time
- **Supply chain risk:** `.env` in the build context could be included in the image if a `COPY . .` instruction runs before the final stage
- **Layer invalidation:** Changes to `.md` files invalidate build cache unnecessarily

The Windows `obj/` leak bug mentioned in the curriculum requirement: on Windows with Docker Desktop, MSBuild sometimes writes to `obj/` directories that end up in the COPY context if `.dockerignore` is missing. The NuGet.config `<fallbackPackageFolders><clear /></fallbackPackageFolders>` stops Visual Studio from resolving packages from machine-wide cache paths that would not exist inside the container.

---

## NuGet.config — CI Hygiene

`NuGet.config` at the repo root:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value=".nuget/packages" />
  </config>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <fallbackPackageFolders>
    <clear />
  </fallbackPackageFolders>
</configuration>
```

`<packageSources><clear />` ensures only `nuget.org` is used — no machine-specific feeds. `<fallbackPackageFolders><clear />` prevents Visual Studio from resolving packages from the machine-wide fallback cache (`%APPDATA%\NuGet\...`), which would cause `dotnet restore` inside the Docker container to fail because those paths do not exist.

`globalPackagesFolder = ".nuget/packages"` keeps the package cache inside the repo directory — consistent between CI and local, cacheable by GitHub Actions' `actions/cache`.

---

## CI Pipeline — GitHub Actions

Full file: `.github/workflows/ci.yml`

```yaml
name: CI — Build and Test

on:
  push:
    branches: [main, develop]
  pull_request:
    branches: [main, develop]

jobs:
  build-and-test:
    runs-on: ubuntu-latest

    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "9.0.x"
      - run: dotnet restore
      - run: dotnet build -c Release --no-restore
      - run: dotnet test -c Release --no-build --verbosity normal
                --logger "trx;LogFileName=test-results.trx"
      - uses: actions/upload-artifact@v4
        if: always()
        with:
          name: test-results
          path: "**/TestResults/*.trx"
```

**Step-by-step:**

1. **`checkout@v4`** — clones the repo at the commit that triggered the workflow.

2. **`setup-dotnet@v4`** — installs .NET 9.0.x SDK. This respects `global.json` — the `9.0.x` pattern matches the SDK pinned in `global.json`.

3. **`dotnet restore`** — downloads NuGet packages. In a production pipeline, add `--locked-mode` to fail if `packages.lock.json` differs from the actual package resolution. This prevents supply chain attacks where a malicious package version matches a floating version constraint.

4. **`dotnet build -c Release --no-restore`** — compiles in Release mode without re-running restore. Release mode enables JIT optimizations and strips debug symbols. `TreatWarningsAsErrors=true` could be added here for strictness.

5. **`dotnet test -c Release --no-build`** — runs all test projects discovered in the solution. `--no-build` skips redundant compilation. The TRX logger produces XML test results for GitHub's test annotation rendering.

6. **`upload-artifact` with `if: always()`** — uploads test results even if the test step fails. This gives you the full test failure report without re-running the pipeline.

**Why `ubuntu-latest`:** Docker image builds, `dotnet` CLI, and bash scripts all work correctly on Ubuntu. Windows runners are available but slower and more expensive. The code is cross-platform (.NET 9 runs on Linux/macOS/Windows) so Ubuntu is the correct choice for CI.

**Comparison to GitLab CI:**
```yaml
# GitLab CI equivalent
test:
  image: mcr.microsoft.com/dotnet/sdk:9.0
  script:
    - dotnet restore
    - dotnet build -c Release --no-restore
    - dotnet test -c Release --no-build
```
Same concepts, different YAML schema. The key difference: GitHub Actions uses pre-built action steps (`uses: actions/checkout@v4`) which handle OS-specific edge cases. GitLab CI runs raw shell commands.

---

## Image Tagging Discipline

Current practice in `deploy/azure/deploy.ps1`:
```powershell
docker tag mdh-analytics-api:latest $DockerHubUser/mdh-analytics-api:latest
docker push $DockerHubUser/mdh-analytics-api:latest
```

**The `:latest` problem:** Every push overwrites `:latest`. If you need to roll back (the current `:latest` is broken), you must rebuild the previous commit. The previous `:latest` is gone.

**Production practice: always push both:**
```powershell
$GitSha = git rev-parse --short HEAD
docker tag mdh-analytics-api:latest $DockerHubUser/mdh-analytics-api:latest
docker tag mdh-analytics-api:latest $DockerHubUser/mdh-analytics-api:$GitSha
docker push $DockerHubUser/mdh-analytics-api:latest
docker push $DockerHubUser/mdh-analytics-api:$GitSha
```

Now you can roll back by updating the Container App image tag to the previous `:<git-sha>`. Rollback becomes:
```bash
az containerapp update --name ca-mdh-analytics-api -g rg-mdh \
  --image ramiro10490/mdh-analytics-api:abc1234
```

**Semantic versioning for libraries:** For NuGet packages or versioned APIs, use semver (`v1.2.3`). For continuously deployed backend services, git SHA or timestamp (`20240615.1`) is more precise and unambiguous.

---

## Secrets in CI/CD

**GitHub Secrets** (Settings → Secrets and variables → Actions): Encrypted at rest, masked in logs. Reference as `${{ secrets.MY_SECRET }}`. This is the correct mechanism for API keys, Docker Hub tokens, and Azure credentials in CI.

**OIDC Federation to Azure (production standard):** Instead of storing an Azure service principal client secret in GitHub Secrets, configure OIDC federation. GitHub Actions gets a short-lived JWT token from GitHub's OIDC provider, exchanges it for an Azure access token. No long-lived credentials stored anywhere. Setup:
```bash
az ad app federated-credential create --id $APP_ID --parameters @oidc.json
```

**What NOT to commit:**
- `.env` files
- `infra/main.parameters.json` (already in `.gitignore`)
- Any file with `sk-ant-`, `password=`, `secret=` in it

Add [gitleaks](https://github.com/gitleaks/gitleaks) as a pre-commit hook to catch accidental credential commits before they reach the repo.

---

## Supply Chain Security Primer

**SBOM (Software Bill of Materials):** A machine-readable list of all components in a software artifact (direct + transitive dependencies). Enables querying "is any component in this image affected by CVE-XXXX?" Tools: `dotnet sbom-tool`, `syft`.

**Image signing (cosign):** Cryptographically signs container images so consumers can verify they came from your CI pipeline and were not tampered with in the registry. Used at Google, Chainguard.

**Vulnerability scanning (trivy, Snyk, GitHub Dependabot):** Scans container images and NuGet packages for known CVEs. GitHub Dependabot is already enabled if you enable it in repo settings — it sends automated PRs to bump vulnerable package versions.

**MDH current state:** No SBOM, no signing, no active vuln scanning (Dependabot alerts enabled by default). Production upgrade: add `trivy image $IMAGE` to the CI pipeline as a gate that fails on HIGH severity CVEs.

---

## Exercise

1. Open `src/MDH.IngestionService/Dockerfile` line 15. The `dotnet restore` layer is separate from the source copy. Delete `Directory.Packages.props` locally, rebuild the image, and observe which layer cache is invalidated. Why?

2. The CI pipeline uses `--no-restore` in the build step and `--no-build` in the test step. Why are these flags safe? What would happen if you removed them?

3. An engineer adds `ENV DEBUG_SECRET=sk-ant-abc123` to a Dockerfile. Even if the Dockerfile is later updated to remove that line, why might the secret still be accessible? (Hint: think about Docker image layers.)

4. Write the GitHub Actions step that would run `trivy image` on the built image and fail the CI pipeline if any HIGH or CRITICAL CVE is found.

---

## Common mistakes

- **Putting secrets in Dockerfile ENV instructions.** Even if removed in a later layer, secrets baked into any layer are recoverable via `docker history --no-trunc`. Secrets must come from the environment at runtime, never baked into the image.

- **Multi-stage build without copying only the publish output.** If you copy the entire `/src` directory from the build stage to the final stage, all source files (including `.csproj`, C# files) end up in the production image. An attacker who compromises the running container gets your source code. Copy only `/app/publish`.

- **Using the shell form for ENTRYPOINT.** `ENTRYPOINT dotnet MyApp.dll` (shell form) spawns `sh` as PID 1. SIGTERM goes to `sh`, which may not forward it to the .NET process. Use `ENTRYPOINT ["dotnet", "MyApp.dll"]` (exec form) so the .NET process is PID 1 and receives SIGTERM directly.

- **Not pinning action versions in GitHub Actions.** `uses: actions/checkout@main` uses whatever `main` points to at the time — a supply chain attack vector (someone pushes a malicious commit to `main`). Use `uses: actions/checkout@v4` (or pin to a specific commit SHA for maximum security).

- **Ignoring `dotnet test` exit code in CI.** If `dotnet test` returns non-zero, the CI step should fail. Most CI systems handle this automatically, but a script that runs `dotnet test; echo done` ignores the exit code. Use `set -e` in bash or PowerShell's `$ErrorActionPreference = "Stop"`.

---
## Case Study — The Placeholder That Wasn't

During the first Azure deployment, `infra/main.parameters.example.json` shipped with this value for `sqlAdminPassword`:

    "value": "REPLACE_WITH_STRONG_PASSWORD_Min8_Uppercase_Digit_Symbol"

The string is meant to be obviously a placeholder. But it accidentally satisfies Azure SQL's complexity policy: 8+ chars, uppercase, digit, symbol (the underscores count). When the file was copied to `infra/main.parameters.json` without the value being changed, Bicep accepted the placeholder verbatim and Azure SQL set it as the real admin password. The connection string secret then got the same literal value. The whole stack worked — until I noticed the placeholder string was already in public git history.

**Four lessons from this:**

1. **Placeholders must be syntactically invalid for the field they shape.** `<<set-via-deploy>>` cannot pass any password complexity rule. `REPLACE_WITH_STRONG_PASSWORD_...` can.
2. **Pre-deploy validation should reject suspicious values for `@secure()` parameters.** A regex run against the parameter file before `az deployment` would catch this: anything matching `/REPLACE|TODO|CHANGE_?ME|<<.*>>/i` aborts the deploy.
3. **Treat git history as adversarial.** Once a value enters `git log`, it is compromised even if rewritten later. The fix is to rotate the secret, not to rewrite history.
4. **The pattern that prevents this whole class of bug:** `main.parameters.example.json` is committed with explicit invalid markers; `main.parameters.json` is `.gitignore`d and contains real values locally; CI pipelines inject real values from GitHub Secrets / Key Vault at deploy time. Local dev never touches CI secrets.

The rotation procedure when this kind of leak is detected:

1. Generate strong random password (24 chars, ≥4 symbols, **shell-safe character set** to avoid pipe / redirect collisions in CLI invocations).
2. `az sql server update --admin-password <new>` to change it on the server. Verify exit code, never trust a green print without a real check.
3. `az containerapp secret set sql-connection-string=<new-conn-string>` for each consuming service.
4. `az containerapp revision restart` to pick up the new secret.
5. Verify `/health` returns 200 across all services.
6. Commit the cleaned-up parameters file with `<<...>>` markers.
## Interview Angle — Smart Apartment Data

1. **"Walk me through your Dockerfile."** — Multi-stage build: SDK stage for compilation (layer cache optimized — csproj files copied before source), runtime stage for execution (200MB vs 800MB SDK). Non-root user for security. ENTRYPOINT in exec form so the .NET process is PID 1 and receives SIGTERM for graceful shutdown. `.dockerignore` prevents build context bloat and accidental secret inclusion.

2. **"What does your CI pipeline do?"** — checkout → setup-dotnet 9.0.x → restore → build Release → test. Test results uploaded as artifact. Tests run on Ubuntu (cross-platform). No deploy step — deploys are triggered manually. This is intentional — one-click deploys to production require a human confirmation step.

3. **"How do you handle container image rollbacks?"** — Tag every image with both `:latest` and `:<git-sha>`. Rolling back is `az containerapp update --image <repo>:<old-sha>`. `:latest` alone makes rollback require a rebuild.

4. **"What is a supply chain attack and how do you defend against it?"** — A supply chain attack compromises the software before it reaches your users: a malicious NuGet package, a tampered Docker base image, a compromised GitHub Action. Defenses: pin action versions to SHAs, use Microsoft's official base images, enable Dependabot for CVE scanning, add SBOM generation to CI, consider image signing (cosign).

5. **30-second talking point:** "The Dockerfiles use multi-stage builds: csproj-first layer caching so NuGet restore is cached independently of source changes, SDK stage for compilation, runtime-only final stage to keep images lean (~200MB vs ~800MB). CI is GitHub Actions: restore, build Release, test, upload TRX results. Image tagging uses both :latest and :<git-sha> for reproducible rollbacks."

6. **Job requirement proof:** "CI/CD" — GitHub Actions pipeline with build + test on every push; Docker multi-stage builds with layer caching; image tagging strategy documented.
