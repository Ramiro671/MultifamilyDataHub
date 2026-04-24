using MDH.IngestionService.Models;
using MDH.Shared.Contracts;
using MongoDB.Driver;

namespace MDH.IngestionService.Persistence;

public class RawListingStore : IRawListingStore
{
    private readonly IMongoCollection<RawListing> _collection;
    private readonly ILogger<RawListingStore> _logger;

    public RawListingStore(IMongoClient mongoClient, IConfiguration config, ILogger<RawListingStore> logger)
    {
        _logger = logger;
        var dbName = config["Mongo:Database"] ?? "mdh_raw";
        var db = mongoClient.GetDatabase(dbName);
        _collection = db.GetCollection<RawListing>("listings_raw");

        // Ensure index on processed flag and external_id
        var processedIdx = Builders<RawListing>.IndexKeys.Ascending(x => x.Processed);
        var externalIdx = Builders<RawListing>.IndexKeys.Ascending(x => x.ExternalId);
        _collection.Indexes.CreateMany(
        [
            new CreateIndexModel<RawListing>(processedIdx),
            new CreateIndexModel<RawListing>(externalIdx, new CreateIndexOptions { Unique = false })
        ]);
    }

    public async Task InsertManyAsync(IEnumerable<object> documents, CancellationToken ct = default)
    {
        var listings = documents.Cast<RawListing>().ToList();
        await _collection.InsertManyAsync(listings, cancellationToken: ct);
        // 🔴 BREAKPOINT: raw batch persisted
        _logger.LogInformation("Persisted {Count} raw listings to MongoDB", listings.Count);
    }

    public async Task<IReadOnlyList<T>> GetUnprocessedAsync<T>(int batchSize, CancellationToken ct = default)
    {
        var filter = Builders<RawListing>.Filter.Eq(x => x.Processed, false);
        var docs = await _collection
            .Find(filter)
            .Limit(batchSize)
            .ToListAsync(ct);
        return docs.Cast<T>().ToList().AsReadOnly();
    }

    public async Task MarkProcessedAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        var filter = Builders<RawListing>.Filter.In(x => x.Id, idList);
        var update = Builders<RawListing>.Update.Set(x => x.Processed, true);
        await _collection.UpdateManyAsync(filter, update, cancellationToken: ct);
    }
}
