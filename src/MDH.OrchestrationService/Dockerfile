# syntax=docker/dockerfile:1
# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy central NuGet and build props first so restore layer is cached
COPY Directory.Packages.props Directory.Build.props ./

# Copy project files and restore (cached unless deps change)
COPY src/MDH.Shared/MDH.Shared.csproj                               src/MDH.Shared/
COPY src/MDH.OrchestrationService/MDH.OrchestrationService.csproj   src/MDH.OrchestrationService/
RUN dotnet restore src/MDH.OrchestrationService/MDH.OrchestrationService.csproj

# Copy full source and publish
COPY src/MDH.Shared/              src/MDH.Shared/
COPY src/MDH.OrchestrationService/ src/MDH.OrchestrationService/
RUN dotnet publish src/MDH.OrchestrationService/MDH.OrchestrationService.csproj \
    -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Non-root user for security
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

EXPOSE 5020
ENTRYPOINT ["dotnet", "MDH.OrchestrationService.dll"]
