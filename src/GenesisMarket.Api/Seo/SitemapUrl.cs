namespace GenesisMarket.Api.Seo;

/// <summary>
/// Одна запись карты сайта. Кроме адреса и подсказок краулеру в sitemap не попадает ничего:
/// ни владельца, ни контактов, ни служебных полей объявления — только публичный URL и дата
/// последнего изменения.
/// </summary>
/// <param name="Loc">Абсолютный адрес страницы.</param>
/// <param name="LastMod">Момент последнего изменения; null — если у страницы его нет (статика).</param>
/// <param name="ChangeFreq">Ожидаемая частота изменений (значения протокола, см. <see cref="SitemapChangeFreq"/>).</param>
/// <param name="Priority">Приоритет 0.0..1.0 относительно других страниц этого же сайта.</param>
public readonly record struct SitemapUrl(
    string Loc,
    DateTimeOffset? LastMod,
    string ChangeFreq,
    decimal Priority);

/// <summary>Значения &lt;changefreq&gt; протокола sitemap 0.9, которые использует площадка.</summary>
public static class SitemapChangeFreq
{
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
}
