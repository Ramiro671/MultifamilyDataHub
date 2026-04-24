using MDH.IngestionService;
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

    // MongoDB
    var mongoConn = builder.Configuration.GetConnectionString("MongoDB")
        ?? builder.Configuration["MONGO_CONNECTION_STRING"]
        ?? "mongodb://mdh:mdh@localhost:27017";
    builder.Services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConn));
    builder.Services.AddSingleton<IRawListingStore, RawListingStore>();

    builder.Services.AddHostedService<IngestionWorker>();

    builder.Services.AddHealthChecks();
    builder.WebHost.UseUrls("http://+:5010");

    var app = builder.Build();
    app.MapHealthChecks("/health");

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
