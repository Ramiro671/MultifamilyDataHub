using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MDH.OrchestrationService.Persistence.Entities;

[Table("dim_submarket", Schema = "warehouse")]
public class DimSubmarket
{
    [Key]
    public int SubmarketId { get; set; }

    [Required, MaxLength(100)]
    public string Name { get; set; } = default!;

    [MaxLength(50)]
    public string State { get; set; } = default!;

    [MaxLength(50)]
    public string Region { get; set; } = default!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<DimListing> Listings { get; set; } = new List<DimListing>();
    public ICollection<FactMarketMetrics> MarketMetrics { get; set; } = new List<FactMarketMetrics>();
}
