namespace GenesisMarket.Api.Seo;

/// <summary>
/// Настройки индексации/SEO. Секция <c>Seo</c>. Публичный адрес фронтенда нужен для
/// канонических ссылок, sitemap, og-тегов и JSON-LD — без него краулеру нечего отдать,
/// поэтому эндпоинты SEO при пустом <see cref="WebBaseUrl"/> возвращают 503.
/// </summary>
public sealed class SeoOptions
{
    public const string Section = "Seo";

    /// <summary>
    /// Базовый публичный адрес сайта (<c>Seo__WebBaseUrl</c>), например
    /// <c>https://genesis-market.pmr</c>. Все ссылки в meta/sitemap/robots — абсолютные от него.
    /// </summary>
    public string WebBaseUrl { get; set; } = "";

    /// <summary>Название площадки для суффикса &lt;title&gt; и поля seller/publisher в JSON-LD.</summary>
    public string SiteName { get; set; } = "Genesis Market";

    /// <summary>
    /// Срок жизни presigned-ссылки на og:image превью. Длинный намеренно: og-картинку
    /// кэшируют соцсети/поисковики, короткий TTL давал бы «битые» превью в выдаче.
    /// </summary>
    public int OgImageTtlDays { get; set; } = 7;

    /// <summary>
    /// Порог, при превышении которого <c>/sitemap.xml</c> становится sitemap-index
    /// с разбивкой (а не одним &lt;urlset&gt;). По протоколу sitemap лимит — 50 000 URL.
    /// </summary>
    public int SitemapSplitThreshold { get; set; } = 45_000;

    /// <summary>Размер одной страницы sitemap объявлений при разбивке (URL на файл).</summary>
    public int SitemapPageSize { get; set; } = 40_000;

    /// <summary>Время кэширования sitemap-ответов (Cache-Control max-age, секунды).</summary>
    public int SitemapCacheSeconds { get; set; } = 3600;

    /// <summary>Базовый адрес без хвостового слэша. Пусто ⇒ null (SEO-эндпоинты недоступны).</summary>
    public string? NormalizedBaseUrl =>
        string.IsNullOrWhiteSpace(WebBaseUrl) ? null : WebBaseUrl.TrimEnd('/');
}
