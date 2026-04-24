using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace MDH.IngestionService.Models;

public class RawListing
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

    [BsonElement("correlation_id")]
    public string CorrelationId { get; set; } = default!;
}
