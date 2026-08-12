using System.Text.Json.Nodes;
using GenesisMarket.Api.Contracts;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Entities;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Seo;

/// <summary>
/// Чистая сборка SEO-мета объявления (title/description/og/JSON-LD). Без БД и HTTP —
/// принимает готовые данные, поэтому легко тестируется и не дублируется на фронте.
/// </summary>
public static class ListingMetaBuilder
{
    /// <summary>Предел длины meta description для сниппета в выдаче.</summary>
    private const int MaxDescriptionLength = 160;

    /// <summary>
    /// У рубля ПМР нет ISO-4217 кода. В JSON-LD ставим непустой <c>priceCurrency</c> «RUP»
    /// (не RUB/MDL — это другие валюты), а человеку поясняем валюту текстом в description.
    /// </summary>
    public const string PriceCurrency = "RUP";

    private const string CurrencyNote = "Цена указана в рублях ПМР (RUP).";

    /// <summary>Основные данные объявления, нужные для мета (без навигаций EF).</summary>
    public readonly record struct MetaInput(
        string Title,
        string Description,
        decimal? Price,
        PriceType PriceType,
        Category Category,
        City City,
        Condition Condition,
        ListingStatus Status,
        string SellerName);

    public static ListingMetaResponse Build(
        MetaInput listing, string canonicalUrl, string? ogImage, string siteName)
    {
        var noIndex = listing.Status != ListingStatus.Active;
        var isArchived = listing.Status is ListingStatus.Archived or ListingStatus.Sold;

        var priceLabel = FormatPrice(listing.PriceType, listing.Price);
        var cityLabel = CatalogLabels.City(listing.City);

        // title: «Заголовок — 15 000 руб. — Тирасполь | Genesis Market».
        var title = $"{listing.Title} — {priceLabel} — {cityLabel} | {siteName}";
        var description = BuildDescription(listing.Description, listing.Title, cityLabel, priceLabel);

        return new ListingMetaResponse(
            Title: title,
            Description: description,
            CanonicalUrl: canonicalUrl,
            OgTitle: $"{listing.Title} — {priceLabel}",
            OgDescription: description,
            OgImage: ogImage,
            IsArchived: isArchived,
            NoIndex: noIndex,
            JsonLd: BuildJsonLd(listing, canonicalUrl, ogImage, siteName));
    }

    /// <summary>meta description: текст объявления, схлопнутые пробелы, обрезка по границе слова.</summary>
    private static string BuildDescription(string raw, string title, string cityLabel, string priceLabel)
    {
        var text = CollapseWhitespace(raw);
        if (string.IsNullOrEmpty(text))
            // Пустое описание (теоретически невозможно из-за валидации) — собираем осмысленный фоллбек.
            text = $"{title}. {priceLabel}. {cityLabel}.";
        return Truncate(text, MaxDescriptionLength);
    }

    private static JsonNode BuildJsonLd(MetaInput listing, string canonicalUrl, string? ogImage, string siteName)
    {
        var offers = new JsonObject
        {
            ["@type"] = "Offer",
            ["priceCurrency"] = PriceCurrency,
            ["availability"] = Availability(listing.Status),
            ["url"] = canonicalUrl
        };

        // Конкретную цену отдаём для Fixed/Free; для договорной (Negotiable) поля price нет.
        if (PriceValue(listing.PriceType, listing.Price) is { } price)
            offers["price"] = price;

        if (ItemCondition(listing.Condition) is { } condition)
            offers["itemCondition"] = condition;

        offers["seller"] = new JsonObject
        {
            ["@type"] = "Person",
            ["name"] = listing.SellerName
        };

        var product = new JsonObject
        {
            ["@context"] = "https://schema.org",
            ["@type"] = "Product",
            ["name"] = listing.Title,
            ["description"] = JsonLdDescription(listing),
            ["category"] = CatalogLabels.Category(listing.Category),
            ["offers"] = offers
        };

        if (ogImage is not null)
            product["image"] = new JsonArray(ogImage);

        return product;
    }

    /// <summary>Описание для JSON-LD: текст объявления + пояснение валюты (когда цена есть).</summary>
    private static string JsonLdDescription(MetaInput listing)
    {
        var text = CollapseWhitespace(listing.Description);
        if (string.IsNullOrEmpty(text))
            text = listing.Title;
        text = Truncate(text, 480);
        return PriceValue(listing.PriceType, listing.Price) is not null ? $"{text} {CurrencyNote}" : text;
    }

    /// <summary>Человекочитаемая цена для title/og (рубли ПМР).</summary>
    private static string FormatPrice(PriceType priceType, decimal? price) => priceType switch
    {
        PriceType.Free => "Бесплатно",
        PriceType.Negotiable => "Цена договорная",
        _ => $"{price:N0} руб."
    };

    /// <summary>Числовая цена для offers.price. null для договорной (в JSON-LD поле опускаем).</summary>
    private static decimal? PriceValue(PriceType priceType, decimal? price) => priceType switch
    {
        PriceType.Free => 0m,
        PriceType.Negotiable => null,
        _ => price
    };

    private static string Availability(ListingStatus status) => status switch
    {
        ListingStatus.Active => "https://schema.org/InStock",
        ListingStatus.Sold => "https://schema.org/SoldOut",
        _ => "https://schema.org/Discontinued"
    };

    private static string? ItemCondition(Condition condition) => condition switch
    {
        Condition.New => "https://schema.org/NewCondition",
        Condition.Used => "https://schema.org/UsedCondition",
        _ => null
    };

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Обрезка до предела по границе слова с добавлением многоточия.</summary>
    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
            return text;
        var cut = text[..max];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > max / 2)
            cut = cut[..lastSpace];
        return cut.TrimEnd(' ', ',', '.', ';', ':', '-') + "…";
    }
}
