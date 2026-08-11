namespace GenesisMarket.Api.Listings;

/// <summary>Анти-скрейпинг раскрытия контактов. Секция <c>ContactReveal</c>.</summary>
public sealed class ContactRevealOptions
{
    public const string Section = "ContactReveal";

    /// <summary>Лимит раскрытий в час для анонимов (на IpHash).</summary>
    public int AnonPerHour { get; set; } = 10;

    /// <summary>Лимит раскрытий в час для авторизованного пользователя.</summary>
    public int UserPerHour { get; set; } = 30;

    /// <summary>Нижняя граница задержки ответа анонимам, мс.</summary>
    public int MinDelayMs { get; set; } = 300;

    /// <summary>Верхняя граница задержки ответа анонимам, мс.</summary>
    public int MaxDelayMs { get; set; } = 600;

    /// <summary>Порог алерта: больше стольких раскрытий с одного IpHash за час → warning в лог.</summary>
    public int AlertThresholdPerHour { get; set; } = 50;
}
