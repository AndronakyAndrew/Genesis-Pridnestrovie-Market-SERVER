namespace GenesisMarket.Domain.Entities;

/// <summary>
/// Переход на объявление по внешней ссылке (пост в Telegram-канале и т. п.). Append-only
/// журнал для подсчёта переходов по источникам. Как и <see cref="ContactReveal"/>, IP хранится
/// ТОЛЬКО как HMAC-SHA256 (<see cref="IpHash"/>) — сырой IP в базу не попадает никогда.
/// </summary>
public class LinkClick
{
    /// <summary>Суррогатный ключ — identity: журнал растёт быстро, UUID тут лишний вес.</summary>
    public long Id { get; set; }

    /// <summary>Объявление, на которое перешли.</summary>
    public Guid ListingId { get; set; }

    /// <summary>Источник перехода, до 32 символов: <c>tg</c> и т. п.</summary>
    public required string Source { get; set; }

    /// <summary>
    /// HMAC-SHA256 от IP (hex) — тем же механизмом, что и у <see cref="ContactReveal"/>
    /// (<c>IIpHasher</c> в Api). Сырой IP не хранится.
    /// </summary>
    public required string IpHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
