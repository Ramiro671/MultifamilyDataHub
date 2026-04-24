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

    // EF Core
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

    builder.Services.AddHealthChecks();
    builder.WebHost.UseUrls("http://+:5020");

    var app = builder.Build();

    // Auto-migrate on startup
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        db.Database.Migrate();
    }

    app.UseHangfireDashboard("/hangfire");
    app.MapHealthChecks("/health");

    // Register recurring jobs
    RecurringJob.AddOrUpdate<CleanListingsJob>(
        "clean-listings",
        job => job.ExecuteAsync(),
        "*/30 * * * * *"); // every 30 seconds (Cron seconds)

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
