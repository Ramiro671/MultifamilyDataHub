using MDH.Shared.Contracts;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MDH.OrchestrationService.Persistence;

// Local representation of the raw listing document for the ETL reader
public class RawListingDocument
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    [BsonElement("external_id")]
    public string ExternalId { get; set; } = default!;

    [BsonElement("submarket")]
    public string Submarket { get; set; } = default!;

    [BsonElement("street_address")]
    public string StreetAddress { get; set; } = default!;

    [BsonElement("unit")]
    public string Unit { get; set; } = default!;

    [BsonElement("bedrooms")]
    public int Bedrooms { get; set; }

    [BsonElement("bathrooms")]
    public decimal Bathrooms { get; set; }

    [BsonElement("sqft")]
    public int Sqft { get; set; }

    [BsonElement("asking_rent")]
    public decimal AskingRent { get; set; }

    [BsonElement("effective_rent")]
    public decimal EffectiveRent { get; set; }

    [BsonElement("concessions")]
    public decimal Concessions { get; set; }

    [BsonElement("operator")]
    public string Operator { get; set; } = default!;

    [BsonElement("scraped_at")]
    public DateTime ScrapedAt { get; set; }

    [BsonElement("processed")]
    public bool Processed { get; set; }
}

public class MongoRawListingStore : IRawListingStore
{
    private readonly IMongoCollection<RawListingDocument> _collection;
    private readonly ILogger<MongoRawListingStore> _logger;

    public MongoRawListingStore(IMongoClient mongoClient, IConfiguration config, ILogger<MongoRawListingStore> logger)
    {
        _logger = logger;
        var dbName = config["Mongo:Database"] ?? "mdh_raw";
        var db = mongoClient.GetDatabase(dbName);
        _collection = db.GetCollection<RawListingDocument>("listings_raw");
    }

    public Task InsertManyAsync(IEnumerable<object> documents, CancellationToken ct = default)
        => throw new NotSupportedException("Orchestration service only reads from Mongo");

    public async Task<IReadOnlyList<T>> GetUnprocessedAsync<T>(int batchSize, CancellationToken ct = default)
    {
        var filter = Builders<RawListingDocument>.Filter.Eq(x => x.Processed, false);
        var docs = await _collection
            .Find(filter)
            .Limit(batchSize)
            .ToListAsync(ct);
        return docs.Cast<T>().ToList().AsReadOnly();
    }

    public async Task MarkProcessedAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        var filter = Builders<RawListingDocument>.Filter.In(x => x.Id, idList);
        var update = Builders<RawListingDocument>.Update.Set(x => x.Processed, true);
        await _collection.UpdateManyAsync(filter, update, cancellationToken: ct);
        _logger.LogDebug("Marked {Count} documents as processed", idList.Count);
    }
}
