using MDH.IngestionService;
using MDH.IngestionService.Health;
using MDH.IngestionService.Persistence;
using MDH.Shared.Contracts;
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
        .WriteTo.File("logs/ingestion-.log", rollingInterval: RollingInterval.Day));

    // MongoDB — MongoClient handles reconnect internally; we expose the connection
    // string via env var (MONGO_CONNECTION_STRING) or ConnectionStrings:MongoDB in
    // appsettings so Container Apps secrets can inject it either way.
    var mongoConn = builder.Configuration.GetConnectionString("MongoDB")
        ?? builder.Configuration["MONGO_CONNECTION_STRING"]
        ?? "mongodb://mdh:mdh@localhost:27017";
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConn));
    builder.Services.AddSingleton<IRawListingStore, RawListingStore>();

    builder.Services.AddHostedService<IngestionWorker>();

    // Liveness: always 200 if process is alive
    // Readiness: pings MongoDB/Cosmos DB via custom check (avoids MongoDB.Driver 2.x compat issue
    // in the AspNetCore.HealthChecks.MongoDb package — we run Driver 3.x)
    builder.Services.AddHealthChecks()
        .AddCheck<MongoHealthCheck>("mongodb", tags: ["ready"]);

    builder.WebHost.UseUrls("http://+:5010");

    var app = builder.Build();

    app.MapHealthChecks("/health");
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    });

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "IngestionService terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
