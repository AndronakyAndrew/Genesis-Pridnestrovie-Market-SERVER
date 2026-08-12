using System.Text.Json.Nodes;

namespace GenesisMarket.Api.Contracts;

/// <summary>
/// Готовые SEO-мета карточки объявления для &lt;head&gt; фронтенда. Собирается на сервере,
/// чтобы фронт (в т.ч. SSR/краулер) не дублировал логику формирования title/description/JSON-LD.
/// </summary>
public record ListingMetaResponse(
    /// <summary>Содержимое &lt;title&gt;.</summary>
    string Title,
    /// <summary>meta[name=description] / og:description.</summary>
    string Description,
    /// <summary>Канонический адрес карточки (&lt;link rel=canonical&gt;).</summary>
    string CanonicalUrl,
    string OgTitle,
    string OgDescription,
    /// <summary>Абсолютный URL превью для og:image. null — у объявления нет фото.</summary>
    string? OgImage,
    /// <summary>
    /// true ⇒ страница снята с публикации (архив/продано): фронт обязан поставить
    /// &lt;meta name=robots content=noindex&gt;. Продублировано явным <see cref="NoIndex"/>.
    /// </summary>
    bool IsArchived,
    bool NoIndex,
    /// <summary>Готовый объект schema.org/Product для &lt;script type="application/ld+json"&gt;.</summary>
    JsonNode JsonLd);

/// <summary>
/// Данные для статической посадочной «категория × город» (например, «Купить квартиру в
/// Тирасполе»): счётчик активных объявлений, диапазон цен и топ подкатегорий.
/// </summary>
public record SeoLandingResponse(
    string Category,
    string CategoryLabel,
    string City,
    string CityLabel,
    string CanonicalUrl,
    /// <summary>Число активных объявлений в этой паре (категория × город).</summary>
    long Count,
    /// <summary>Минимальная цена среди активных с указанной ценой (null — таких нет).</summary>
    decimal? PriceFrom,
    /// <summary>Максимальная цена среди активных с указанной ценой (null — таких нет).</summary>
    decimal? PriceTo,
    /// <summary>Подкатегории с наибольшим числом объявлений (по убыванию).</summary>
    IReadOnlyList<LandingSubcategoryResponse> TopSubcategories);

/// <summary>Подкатегория в сводке посадочной: ключ, название и счётчик активных объявлений.</summary>
public record LandingSubcategoryResponse(
    int SubcategoryId,
    string Slug,
    string Name,
    long Count);
