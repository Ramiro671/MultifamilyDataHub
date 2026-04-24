using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MDH.OrchestrationService.Persistence.Entities;

[Table("fact_daily_rent", Schema = "warehouse")]
public class FactDailyRent
{
    [Key]
    public long FactId { get; set; }

    public Guid ListingId { get; set; }

    public DateOnly RentDate { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal AskingRent { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal EffectiveRent { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal Concessions { get; set; }

    [Column(TypeName = "decimal(6,2)")]
    public decimal RentPerSqft { get; set; }

    public DateTime LoadedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ListingId))]
    public DimListing Listing { get; set; } = default!;
}
