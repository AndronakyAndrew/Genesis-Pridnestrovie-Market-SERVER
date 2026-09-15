namespace GenesisMarket.Api.Listings;

/// <summary>Пороги жизненного цикла объявлений. Секция <c>Listings</c>.</summary>
public sealed class ListingOptions
{
    public const string Section = "Listings";

    /// <summary>Максимум «в обороте» (Active + PendingReview) на пользователя.</summary>
    public int MaxActivePerUser { get; set; } = 30;

    /// <summary>Максимальная цена (рубли ПМР).</summary>
    public long MaxPrice { get; set; } = 100_000_000;

    // Пороги «нужна ли модерация» переехали в секцию Moderation (ModerationOptions):
    // решение теперь принимает скоринг автора и контента, а не два счётчика.

    /// <summary>Не чаще одного засчитанного просмотра в стольких минутах на (Listing, IpHash).</summary>
    public int ViewThrottleMinutes { get; set; } = 60;

    /// <summary>
    /// Не чаще одной записи перехода в стольких секундах на (Listing, Source, IpHash).
    /// Редирект <c>/r/l/{id}</c> выведен из-под rate-limit, и без окна любой повтор запроса
    /// был бы строкой в <c>link_clicks</c>. 0 — писать каждый переход.
    /// </summary>
    public int ClickThrottleSeconds { get; set; } = 60;
}
