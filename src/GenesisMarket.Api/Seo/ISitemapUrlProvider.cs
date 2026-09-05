namespace GenesisMarket.Api.Seo;

/// <summary>Раздел карты сайта — источник списка URL.</summary>
public enum SitemapSection
{
    /// <summary>Статика + все активные объявления: один файл, пока URL меньше порога разбивки.</summary>
    All,

    /// <summary>Только статические публичные страницы (главная, каталог, витрины фильтров, инфо).</summary>
    Static,

    /// <summary>Одна страница объявлений (нумерация с 1) для режима sitemap-index.</summary>
    Listings
}

/// <summary>
/// Сборка списка URL для карты сайта. Отделена от генерации XML намеренно: переход на
/// sitemap-index с несколькими файлами меняет только выбор раздела/страницы, а разметку и
/// кэширование трогать не нужно.
/// </summary>
public interface ISitemapUrlProvider
{
    /// <summary>
    /// URL раздела. <paramref name="page"/> учитывается только для
    /// <see cref="SitemapSection.Listings"/> (нумерация с 1), для остальных игнорируется.
    /// </summary>
    Task<IReadOnlyList<SitemapUrl>> GetUrlsAsync(SitemapSection section, int page, CancellationToken ct);

    /// <summary>Сколько объявлений попадёт в карту сайта (активные, не удалённые).</summary>
    Task<long> GetActiveListingCountAsync(CancellationToken ct);

    /// <summary>Сколько статических URL — нужно, чтобы решить, влезает ли всё в один файл.</summary>
    int StaticUrlCount { get; }

    /// <summary>На сколько файлов объявлений разобьётся карта при данном числе объявлений.</summary>
    int ListingPageCount(long activeCount);
}
