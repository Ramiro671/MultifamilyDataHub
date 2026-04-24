using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MDH.OrchestrationService.Persistence.Entities;

[Table("fact_anomaly", Schema = "warehouse")]
public class FactAnomaly
{
    [Key]
    public Guid AnomalyId { get; set; }

    public Guid ListingId { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal AskingRent { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal SubmarketAvgRent { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal StdDev { get; set; }

    [Column(TypeName = "decimal(6,3)")]
    public decimal ZScore { get; set; }

    [MaxLength(500)]
    public string FlagReason { get; set; } = default!;

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    public bool IsResolved { get; set; }

    [ForeignKey(nameof(ListingId))]
    public DimListing Listing { get; set; } = default!;
}
