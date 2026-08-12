using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Outbox.Telegram;

/// <summary>Пометка состояния поста в канале при правке (совпадает со значением <c>mark</c> в payload).</summary>
public static class ChannelMark
{
    public const string Sold = "sold";
    public const string Archived = "archived";
}

/// <summary>
/// Сборка текста поста объявления для Telegram-канала. Только plain text — parse_mode не
/// используем: заголовок/описание пишет пользователь, и надёжно экранировать разметку под
/// MarkdownV2 (18 спецсимволов) не стоит труда; проще не размечать вовсе.
/// </summary>
public static class TelegramPostFormatter
{
    // Предел подписи sendPhoto — 1024 символа; наш пост заведомо короче, но подстрахуемся.
    private const int MaxCaptionLength = 1024;

    /// <summary>Базовый пост: заголовок, цена (руб. ПМР), город, категория, ссылка.</summary>
    public static string BuildPost(
        string title, decimal? price, PriceType priceType, City city, Category category, string url)
    {
        var post = string.Join('\n',
            title,
            FormatPrice(priceType, price),
            $"📍 {CatalogLabels.City(city)} · {CatalogLabels.Category(category)}",
            url);
        return Trim(post);
    }

    /// <summary>Тот же пост с шапкой-пометкой «Продано»/«Снято с публикации» (для правки).</summary>
    public static string WithMark(string basePost, string mark)
    {
        var header = mark switch
        {
            ChannelMark.Sold => "✅ ПРОДАНО",
            ChannelMark.Archived => "⛔ Снято с публикации",
            _ => null
        };
        return header is null ? basePost : Trim($"{header}\n\n{basePost}");
    }

    public static string FormatPrice(PriceType priceType, decimal? price) => priceType switch
    {
        PriceType.Free => "Бесплатно",
        PriceType.Negotiable => "Цена договорная",
        _ => $"{price:N0} руб."
    };

    /// <summary>Абсолютная ссылка на карточку объявления (или относительная, если базовый URL не задан).</summary>
    public static string BuildUrl(string? webBaseUrl, string slug)
    {
        var path = $"/listing/{slug}";
        return string.IsNullOrWhiteSpace(webBaseUrl) ? path : $"{webBaseUrl.TrimEnd('/')}{path}";
    }

    private static string Trim(string text) =>
        text.Length <= MaxCaptionLength ? text : text[..MaxCaptionLength];
}
