using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Driver;

namespace MDH.IngestionService.Health;

/// <summary>
/// Readiness probe that pings the MongoDB/Cosmos DB server.
/// Uses the driver's built-in ping command so no extra NuGet package is needed.
/// </summary>
public sealed class MongoHealthCheck : IHealthCheck
{
    private readonly IMongoClient _client;

    public MongoHealthCheck(IMongoClient client) => _client = client;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.GetDatabase("admin")
                .RunCommandAsync<MongoDB.Bson.BsonDocument>(
                    new MongoDB.Bson.BsonDocument("ping", 1),
                    cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy("MongoDB reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB unreachable", ex);
        }
    }
}
