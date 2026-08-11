using GenesisMarket.Domain.Common;

namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Факт раскрытия контактов продавца по объявлению. Append-only журнал для
/// анти-скрейпинга и агрегата <c>contactRevealCount</c>.
/// IP хранится ТОЛЬКО как HMAC-SHA256 (<see cref="IpHash"/>) — сырой IP в базу
/// не попадает никогда.
/// </summary>
public class ContactReveal : BaseEntity
{
    /// <summary>Объявление, чьи контакты раскрыли.</summary>
    public Guid ListingId { get; set; }

    /// <summary>Кто смотрел (если авторизован). null — аноним.</summary>
    public Guid? ViewerUserId { get; set; }

    /// <summary>HMAC-SHA256 от IP (hex). Сырой IP не хранится.</summary>
    public required string IpHash { get; set; }
}
