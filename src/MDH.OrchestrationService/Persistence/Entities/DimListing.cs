using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MDH.OrchestrationService.Persistence.Entities;

[Table("dim_listing", Schema = "warehouse")]
public class DimListing
{
    [Key]
    public Guid ListingId { get; set; }

    [Required, MaxLength(100)]
    public string ExternalId { get; set; } = default!;

    public int SubmarketId { get; set; }

    [Required, MaxLength(300)]
    public string StreetAddress { get; set; } = default!;

    [MaxLength(50)]
    public string Unit { get; set; } = default!;

    public int Bedrooms { get; set; }

    [Column(TypeName = "decimal(4,1)")]
    public decimal Bathrooms { get; set; }

    public int Sqft { get; set; }

    [MaxLength(200)]
    public string Operator { get; set; } = default!;

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;

    [ForeignKey(nameof(SubmarketId))]
    public DimSubmarket Submarket { get; set; } = default!;

    public ICollection<FactDailyRent> DailyRents { get; set; } = new List<FactDailyRent>();
    public ICollection<FactAnomaly> Anomalies { get; set; } = new List<FactAnomaly>();
}
