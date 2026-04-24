using MDH.IngestionService.Generation;
using MDH.Shared.Contracts;

namespace MDH.IngestionService;

public class IngestionWorker : BackgroundService
{
    private readonly ILogger<IngestionWorker> _logger;
    private readonly IRawListingStore _store;
    private readonly IConfiguration _config;

    public IngestionWorker(ILogger<IngestionWorker> logger, IRawListingStore store, IConfiguration config)
    {
        _logger = logger;
        _store = store;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _config.GetValue<int>("Ingestion:IntervalSeconds", 10);
        _logger.LogInformation("IngestionWorker starting, interval={Interval}s", intervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var correlationId = Guid.NewGuid().ToString();
            try
            {
                _logger.LogInformation("Starting ingestion tick {CorrelationId}", correlationId);
                var batch = ListingFaker.GenerateBatch(1000, correlationId);
                await _store.InsertManyAsync(batch.Cast<object>(), stoppingToken);
                _logger.LogInformation(
                    "Ingestion tick complete. CorrelationId={CorrelationId} Count={Count}",
                    correlationId, batch.Count);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Ingestion tick failed. CorrelationId={CorrelationId}", correlationId);
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }
}
