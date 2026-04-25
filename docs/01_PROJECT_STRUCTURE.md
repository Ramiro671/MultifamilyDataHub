# 01 — Project Structure and Build System

> **Study time:** ~20 minutes
> **Prerequisites:** none

## Why this matters

Before you can explain a single line of business logic, a senior engineer will assess whether you understand the build system you are shipping. A disordered solution structure signals that the engineer does not think about maintainability, reproducibility, or collaboration. Understanding the full .NET solution layout — from `global.json` to `Directory.Packages.props` — tells an interviewer you have written code that survives beyond your laptop.

In a microservices repo like this one, the solution structure also encodes architectural constraints. The fact that `MDH.Shared` is a project reference (not a NuGet package) tells you exactly where the boundary between shared contracts and service-private logic sits. The fact that `Directory.Build.props` enforces nullability project-wide tells you this team values correctness over convenience. These are not accidental choices — they have compilers and colleagues enforcing them.

By the end of this doc you will be able to: (1) explain every top-level file and folder in the repo and why it exists; (2) describe the difference between `Directory.Build.props`, `Directory.Packages.props`, and `global.json` and what problem each solves; (3) walk through adding a new microservice project to this solution end-to-end.

---

## Repository Layout

```
MultifamilyDataHub/
├── .github/workflows/ci.yml        ← GitHub Actions: build + test on every push
├── src/
│   ├── MDH.Shared/                 ← Class library: DTOs, contracts, domain primitives
│   ├── MDH.IngestionService/       ← Worker: writes synthetic listings to MongoDB
│   ├── MDH.OrchestrationService/   ← Worker + Hangfire: ETL jobs → SQL Server
│   ├── MDH.AnalyticsApi/           ← Web API: reads SQL, serves REST endpoints
│   └── MDH.InsightsService/        ← Web API: calls AnalyticsApi + Anthropic Claude
├── tests/
│   ├── MDH.AnalyticsApi.Tests/
│   ├── MDH.OrchestrationService.Tests/
│   └── MDH.Shared.Tests/
├── docker/
│   ├── docker-compose.yml
│   └── init-db/
├── infra/                          ← Bicep templates for Azure deployment
├── deploy/                         ← Deployment scripts (deploy.ps1)
├── docs/                           ← This curriculum
├── global.json                     ← Pins the .NET SDK version
├── Directory.Build.props           ← MSBuild properties applied to all projects
├── Directory.Packages.props        ← Central NuGet version management
├── MultifamilyDataHub.sln          ← Solution file (project path registry)
├── NuGet.config                    ← NuGet feed sources
└── .gitignore / .editorconfig / .env.example
```

The `src/` vs `tests/` split is a widely adopted convention in .NET projects. It allows `dotnet build src/` and `dotnet test tests/` independently, and it makes it easy to exclude test projects from production Docker images (the Dockerfiles only `COPY src/`).

---

## `global.json` — SDK Pinning

`global.json` at the repo root tells the `dotnet` CLI which SDK version to use when running any command in this directory tree. Ours reads:

```json
// global.json
{
  "sdk": {
    "version": "9.0.0",
    "rollForward": "latestMinor"
  }
}
```

Without this file, running `dotnet build` uses whatever SDK is on the PATH — which differs between developer machines and CI agents. This causes "works on my machine" failures when SDK behavior changes between patch versions (nullable diagnostics, warning codes, and language features all shift). `rollForward: latestMinor` means "use 9.0.x but never 9.1 or higher" — you get security patches automatically without risking a language version bump.

A common interview trap: candidates confuse the SDK version (`global.json`) with the target framework (`<TargetFramework>net9.0</TargetFramework>` in csproj). The SDK is the toolchain; the target framework is what the compiled output runs on. You can use SDK 9.0.3 to compile a project targeting `net8.0`.

---

## `Directory.Build.props` — Shared MSBuild Properties

`Directory.Build.props` is a special file that MSBuild discovers by walking up the directory tree from any `.csproj`. Every property declared in it applies to every project under the same tree. Ours:

```xml
<!-- Directory.Build.props -->
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

**`<Nullable>enable</Nullable>`** enables nullable reference types (NRT) for the entire codebase. This is a C# 8+ feature that makes the compiler track whether a reference type can be null. When enabled, `string s = null;` is a compile-time warning (or error) rather than a runtime `NullReferenceException`. It is the single most impactful setting for writing safe C# because it forces you to think about nullability at the declaration site, not at the crash site.

**`<ImplicitUsings>enable</ImplicitUsings>`** adds a project-scoped auto-generated `GlobalUsings.g.cs` file with common BCL namespaces (`System`, `System.Collections.Generic`, `System.Linq`, etc.) so you do not need to write `using System;` in every file. It reduces boilerplate without hiding anything — the generated file lives in `obj/Debug/net9.0/<Project>.GlobalUsings.g.cs` and is fully inspectable.

**`<LangVersion>latest</LangVersion>`** enables all C# language features up to the latest supported by the installed SDK — in this case, C# 13. Without this you would default to the language version associated with the target framework (C# 12 for net9), missing features like `params collections` and `lock` object semantics.

---

## `Directory.Packages.props` — Central Package Management (CPM)

Without CPM, each `.csproj` that wants Serilog declares `<PackageReference Include="Serilog.AspNetCore" Version="8.0.3" />`. In a five-project solution, this means five places to update when a vulnerability is patched. They will drift.

CPM centralizes all version decisions into one file. The pattern is:

**In `Directory.Packages.props`:**
```xml
<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
<PackageVersion Include="Serilog.AspNetCore" Version="8.0.3" />
```

**In each `.csproj` that needs it:**
```xml
<PackageReference Include="Serilog.AspNetCore" />   <!-- No Version attribute! -->
```

The csproj says "I need Serilog", the central props file says "and 8.0.3 is what everyone gets". Attempting to add a `Version` attribute in a `.csproj` when CPM is enabled is a build error, preventing developers from silently pinning different versions in individual projects. See `Directory.Packages.props` in the repo root for all ~30 managed package versions.

---

## `MultifamilyDataHub.sln` — The Solution File

A `.sln` file is a text file listing the relative paths to every `.csproj` in the repository plus some Visual Studio metadata (project GUIDs, build configurations). It serves three purposes: (1) Visual Studio and Rider open it to show the full tree; (2) `dotnet build MultifamilyDataHub.sln` builds all projects in dependency order; (3) `dotnet test MultifamilyDataHub.sln` discovers all test projects. The `.sln` file itself contains no build logic — it is purely a registry of `*.csproj` paths.

---

## Naming Convention — `MDH.<ServiceName>`

All runtime projects follow `MDH.<ServiceName>` and test projects follow `MDH.<ServiceName>.Tests`. This matters for several reasons beyond cosmetics: (1) the root namespace of each project matches, so reflection-based tools (MediatR's `RegisterServicesFromAssemblyContaining<Program>`, xUnit's test discovery, EF Core's `IMigrationsAssembly`) resolve the correct assembly without extra configuration; (2) it makes the class hierarchy in code reviews unambiguous — you always know which service owns a type.

---

## `MDH.Shared` — Shared via Project Reference, Not NuGet

`MDH.Shared` is referenced by all other projects as a project reference:

```xml
<!-- MDH.AnalyticsApi.csproj -->
<ProjectReference Include="..\..\src\MDH.Shared\MDH.Shared.csproj" />
```

This is deliberate. A project reference means: compile and link together, get immediate feedback if a contract changes (you break all callers at compile time), no version mismatches. In a single repository with co-deployed services this is the right choice. If `MDH.Shared` were a published NuGet package, you would add a publish step, a version bump, and potentially have mismatched versions in production (v1.2 of the API talking to v1.1 of the shared contracts). Project references eliminate that class of bugs entirely in a monorepo.

---

## Adding a New Service — End-to-End

```bash
# 1. Create the project
dotnet new web -n MDH.ReportingService -o src/MDH.ReportingService

# 2. Add to the solution
dotnet sln add src/MDH.ReportingService/MDH.ReportingService.csproj

# 3. Reference MDH.Shared
dotnet add src/MDH.ReportingService reference src/MDH.Shared

# 4. Directory.Packages.props handles versions — no Version= in csproj
# 5. Directory.Build.props handles TargetFramework, Nullable, etc.
# 6. Add a Dockerfile under src/MDH.ReportingService/Dockerfile
# 7. Add to docker-compose.yml
# 8. Add a test project under tests/MDH.ReportingService.Tests/
```

Notice: steps 1–8 involve zero changes to `Directory.Build.props`, `Directory.Packages.props`, or `global.json`. The shared infrastructure files are written once and automatically apply.

---

## Exercise

1. Open `Directory.Packages.props` and find the `Polly` version. Now open `src/MDH.InsightsService/MDH.InsightsService.csproj` and confirm there is no `Version=` attribute on the Polly reference. Explain what would happen if you added `Version="7.0.0"` to the csproj.

2. Open `obj/Debug/net9.0/MDH.AnalyticsApi.GlobalUsings.g.cs` and list 5 namespaces that `ImplicitUsings` adds. Which one is most relevant to LINQ?

3. The `global.json` uses `rollForward: latestMinor`. What would happen if a developer installed SDK 10.0.0 and ran `dotnet build`? Would it succeed or fail?

4. Why does `MDH.Shared` not have its own Dockerfile? Under what circumstances would you need one?

---

## Common mistakes

- **Version attribute in csproj when CPM is active.** Adding `Version="X.Y.Z"` in a `.csproj` when `ManagePackageVersionsCentrally=true` is a build error. The fix is to declare the version only in `Directory.Packages.props`.

- **Putting business logic in `MDH.Shared`.** Shared should contain contracts and primitives only. Once it has `WarehouseDbContext` or service-specific behavior, every service takes a dependency on infrastructure it does not use. See [`06_MDH_SHARED.md`](./06_MDH_SHARED.md) for the full rationale.

- **Forgetting `global.json` in CI images.** A CI pipeline that does not respect `global.json` (e.g., a self-hosted runner with SDK 10 installed) will run the wrong toolchain. The fix is `uses: actions/setup-dotnet@v4` with `dotnet-version: "9.0.x"` in every workflow.

- **Adding test projects to `src/`.** Tests under `src/` will be picked up by Dockerfiles that do `COPY src/ .` — inflating image sizes and potentially leaking test secrets. Keep them under `tests/`.

- **Not adding new services to the solution file.** `dotnet build` works on a single project; `dotnet test MultifamilyDataHub.sln` runs all tests. If a new test project is not registered in the `.sln`, CI misses it silently.

---

## Interview Angle — Smart Apartment Data

1. **"How do you keep NuGet versions consistent across multiple microservices?"** — We use Central Package Management (`Directory.Packages.props`). All five projects declare `<PackageReference Include="..." />` without a version. The single props file is the authority. Adding `Version=` in any csproj is a build error — the tool enforces the constraint.

2. **"What does `<Nullable>enable</Nullable>` actually do at runtime?"** — Nothing at runtime. It is a compiler-time annotation system. It teaches the compiler to track whether a reference type might be null and warn when you dereference without checking. It catches `NullReferenceException` at compile time instead of 3am in production.

3. **"Why not publish `MDH.Shared` to a NuGet feed?"** — In a single repo with co-deployed services, project references are strictly better: you get compile-time breakage on contract changes, zero version skew, and no publish ceremony. NuGet packages are the right choice when the consumer is outside your repo (e.g., a client SDK).

4. **"What does `LangVersion: latest` give you that `net9.0` doesn't by default?"** — `net9.0` defaults to C# 12. `latest` enables C# 13 features like `params` with any collection expression, `lock` semantics on `System.Threading.Lock`, and partial properties. In practice, the most valuable thing it enables is staying current with language improvements as they ship.

5. **30-second talking point:** "The build system in this project demonstrates production discipline: SDK version pinned via `global.json`, shared compiler settings via `Directory.Build.props`, all package versions centralized in `Directory.Packages.props`. No developer can silently drift to a different framework, a different language version, or a different dependency version. That's the invisible scaffolding that makes microservices maintainable."

6. **Job requirement proof:** "C# 13 / .NET 9" — confirmed by `global.json` SDK pinning to `9.0.x`, `Directory.Build.props` setting `<TargetFramework>net9.0</TargetFramework>` and `<LangVersion>latest</LangVersion>`.
