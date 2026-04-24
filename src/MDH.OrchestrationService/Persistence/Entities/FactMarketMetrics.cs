using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MDH.OrchestrationService.Persistence.Entities;

[Table("fact_market_metrics", Schema = "warehouse")]
public class FactMarketMetrics
{
    [Key]
    public long MetricId { get; set; }

    public int SubmarketId { get; set; }

    public int Bedrooms { get; set; }

    public DateOnly MetricDate { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal AvgRent { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal MedianRent { get; set; }

    [Column(TypeName = "decimal(6,2)")]
    public decimal RentPerSqft { get; set; }

    [Column(TypeName = "decimal(5,4)")]
    public decimal OccupancyEstimate { get; set; }

    public int SampleSize { get; set; }

    public DateTime ComputedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(SubmarketId))]
    public DimSubmarket Submarket { get; set; } = default!;
}
