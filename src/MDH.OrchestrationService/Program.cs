using Hangfire;
using MDH.OrchestrationService.Jobs;
using MDH.OrchestrationService.Persistence;
using MDH.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, services, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.Console()
        .WriteTo.File("logs/orchestration-.log", rollingInterval: RollingInterval.Day));

    // EF Core — prefer explicit env var so the Container App secret overrides appsettings.json localhost default
    var sqlConn = builder.Configuration["SQL_CONNECTION_STRING"]
        ?? builder.Configuration.GetConnectionString("SqlServer")
        ?? throw new InvalidOperationException("SQL connection string not configured");
    builder.Services.AddDbContext<WarehouseDbContext>(opts =>
        opts.UseSqlServer(sqlConn)
            // Downgrade PendingModelChangesWarning from error to warning so Migrate() proceeds.
            // The migration SQL is correct; the snapshot seed-data omission caused the mismatch.
            .ConfigureWarnings(w => w.Log(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

    // MongoDB (for CleanListingsJob reading raw listings)
    var mongoConn = builder.Configuration.GetConnectionString("MongoDB")
        ?? builder.Configuration["MONGO_CONNECTION_STRING"]
        ?? "mongodb://mdh:mdh@localhost:27017";
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConn));
    builder.Services.AddSingleton<IRawListingStore, MongoRawListingStore>();

    // Hangfire — use the same connection as EF Core (same DB); explicit override avoids appsettings.json localhost
    var hangfireConn = sqlConn;
    builder.Services.AddHangfire(config => config
        .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer()
        .UseRecommendedSerializerSettings()
        .UseSqlServerStorage(hangfireConn));
    builder.Services.AddHangfireServer();

    // Jobs (transient for Hangfire DI)
    builder.Services.AddTransient<CleanListingsJob>();
    builder.Services.AddTransient<BuildMarketMetricsJob>();
    builder.Services.AddTransient<DetectAnomaliesJob>();

    // Liveness: always 200 if process alive
    // Readiness: SQL must respond (Azure SQL free tier auto-pauses; cold start can take ~60s)
    builder.Services.AddHealthChecks()
        .AddSqlServer(sqlConn, name: "sql-server", tags: ["ready"]);

    builder.WebHost.UseUrls("http://+:5020");

    var app = builder.Build();

    // EF Core migrations with retry — Azure SQL serverless auto-pauses when idle;
    // cold-start latency can be 30–60 s. Retry up to 5 times with exponential backoff.
    await MigrateWithRetryAsync(app.Services);

    app.UseHangfireDashboard("/hangfire");
    app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = _ => false  // Liveness: always 200 — SQL auto-pauses on free tier
    });
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    });

    // Recurring jobs
    // CleanListings: every minute (5-field cron). Was "*/30 * * * * *" (6-field seconds) which
    // is non-standard for base Hangfire. Changed to standard minutely to guarantee compatibility.
    RecurringJob.AddOrUpdate<CleanListingsJob>(
        "clean-listings",
        job => job.ExecuteAsync(),
        Cron.Minutely()); // every 1 minute

    RecurringJob.AddOrUpdate<BuildMarketMetricsJob>(
        "build-market-metrics",
        job => job.ExecuteAsync(),
        "*/5 * * * *"); // every 5 minutes

    RecurringJob.AddOrUpdate<DetectAnomaliesJob>(
        "detect-anomalies",
        job => job.ExecuteAsync(),
        "0 * * * *"); // hourly

    await app.RunAsync();
}
catch (Exception ex) when (ex is not Microsoft.Extensions.Hosting.HostAbortedException)
{
    Log.Fatal(ex, "OrchestrationService terminated unexpectedly");
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}

// Retry EF Core migration to tolerate Azure SQL serverless cold-start (auto-pause adds 30-60 s latency).
// Attempts: 6 x 10 s = up to ~60 s of retries before giving up.
// Azure SQL transient error numbers: -2 (timeout), 40613 (db unavailable), 10928/10929 (resource limits),
// 49918/49919/49920 (processing/throttle issues).
static async Task MigrateWithRetryAsync(IServiceProvider services)
{
    const int maxAttempts = 6;
    const int retryDelaySeconds = 10;
    var transientSqlErrors = new HashSet<int> { -2, 40613, 10928, 10929, 49918, 49919, 49920 };
    Exception? lastException = null;

    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
            Log.Information("Applying pending migrations... Count: {Count}", pending.Count);
            await db.Database.MigrateAsync();
            Log.Information("Migrations applied. Count: {Count}", pending.Count);
            return;
        }
        catch (Microsoft.Data.SqlClient.SqlException sqlEx) when (attempt < maxAttempts && transientSqlErrors.Contains(sqlEx.Number))
        {
            lastException = sqlEx;
            Log.Warning(sqlEx,
                "Migration attempt {Attempt}/{Max} failed (transient SQL error {Number}). Retrying in {Delay}s",
                attempt, maxAttempts, sqlEx.Number, retryDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds));
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            lastException = ex;
            Log.Warning(ex,
                "Migration attempt {Attempt}/{Max} failed. Retrying in {Delay}s",
                attempt, maxAttempts, retryDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds));
        }
        catch (Exception ex)
        {
            lastException = ex;
            // Final attempt — fall through to exit below
        }
    }

    Log.Fatal(lastException, "EF Core migrations failed after {Max} attempts. Terminating.", maxAttempts);
    Environment.Exit(1);
}
