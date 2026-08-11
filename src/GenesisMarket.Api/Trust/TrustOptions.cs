namespace GenesisMarket.Api.Trust;

/// <summary>Слой доверия (отзывы + жалобы). Секция конфигурации <c>Trust</c>.</summary>
public sealed class TrustOptions
{
    public const string Section = "Trust";

    /// <summary>Лимит жалоб в час на один IP (для анонимов).</summary>
    public int IpReportsPerHour { get; set; } = 5;

    /// <summary>Лимит жалоб в час на авторизованного пользователя.</summary>
    public int UserReportsPerHour { get; set; } = 20;

    /// <summary>
    /// Порог автоматики: сколько независимых жалоб Fraud/Prohibited на одно
    /// объявление переводят его в PendingReview и в начало очереди модерации.
    /// </summary>
    public int AutoFlagThreshold { get; set; } = 3;

    /// <summary>Приоритет очереди, присваиваемый автоматически помеченному объявлению.</summary>
    public int AutoFlagPriority { get; set; } = 100;
}
