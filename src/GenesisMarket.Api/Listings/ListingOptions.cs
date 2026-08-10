namespace GenesisMarket.Api.Listings;

/// <summary>Пороги жизненного цикла объявлений. Секция <c>Listings</c>.</summary>
public sealed class ListingOptions
{
    public const string Section = "Listings";

    /// <summary>Максимум «в обороте» (Active + PendingReview) на пользователя.</summary>
    public int MaxActivePerUser { get; set; } = 30;

    /// <summary>Максимальная цена (рубли ПМР).</summary>
    public long MaxPrice { get; set; } = 100_000_000;

    /// <summary>Меньше стольких опубликованных объявлений ⇒ премодерация.</summary>
    public int MinPublishedForAutoActive { get; set; } = 3;

    /// <summary>Аккаунт моложе стольких дней ⇒ премодерация.</summary>
    public int MinAccountAgeDays { get; set; } = 7;

    /// <summary>Не чаще одного засчитанного просмотра в стольких минутах на (Listing, IpHash).</summary>
    public int ViewThrottleMinutes { get; set; } = 60;
}
