using GenesisMarket.Domain.Common;

namespace GenesisMarket.Domain.Entities;

/// <summary>Точка самовывоза магазина; расписание хранится одной JSONB-строкой.</summary>
public sealed class ShopPickupPoint : BaseEntity
{
    public Guid OwnerId { get; set; }
    public string City { get; set; } = null!;
    public string Address { get; set; } = null!;
    public string? Note { get; set; }
    public bool IsMain { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>Нормализованное представление расписания; null — часы не заданы.</summary>
    public string? Hours { get; set; }
}
