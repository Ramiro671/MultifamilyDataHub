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

    // EF Core — connection string via env var or ConnectionStrings section
    var sqlConn = builder.Configuration.GetConnectionString("SqlServer")
        ?? builder.Configuration["SQL_CONNECTION_STRING"]
        ?? throw new InvalidOperationException("SQL connection string not configured");
    builder.Services.AddDbContext<WarehouseDbContext>(opts =>
        opts.UseSqlServer(sqlConn));

    // MongoDB (for CleanListingsJob reading raw listings)
    var mongoConn = builder.Configuration.GetConnectionString("MongoDB")
        ?? builder.Configuration["MONGO_CONNECTION_STRING"]
        ?? "mongodb://mdh:mdh@localhost:27017";
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConn));
    builder.Services.AddSingleton<IRawListingStore, MongoRawListingStore>();

    // Hangfire
    var hangfireConn = builder.Configuration.GetConnectionString("HangfireStorage") ?? sqlConn;
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
    app.MapHealthChecks("/health");
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
catch (Exception ex)
{
    Log.Fatal(ex, "OrchestrationService terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}

// Retry EF Core migration to tolerate Azure SQL serverless cold-start (up to ~60 s).
static async Task MigrateWithRetryAsync(IServiceProvider services)
{
    const int maxAttempts = 6;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
            db.Database.Migrate();
            Log.Information("EF Core migrations applied successfully");
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2, 4, 8, 16, 32 s
            Log.Warning(ex,
                "Migration attempt {Attempt}/{Max} failed (likely SQL cold-start). Retrying in {Delay}s",
                attempt, maxAttempts, delay.TotalSeconds);
            await Task.Delay(delay);
        }
    }
}
