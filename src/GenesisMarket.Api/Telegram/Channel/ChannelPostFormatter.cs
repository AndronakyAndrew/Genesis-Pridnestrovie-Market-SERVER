using System.Globalization;
using GenesisMarket.Api.Listings;
using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Telegram.Channel;

/// <summary>
/// Что из объявления идёт в пост. Контактов продавца здесь нет по построению: ни телефона,
/// ни имени, ни профиля; контакты, вписанные в сам текст, вычищает форматтер.
/// </summary>
public sealed record ChannelPostContent(
    string Title, string? Description, decimal? Price, PriceType PriceType, City City);

/// <summary>
/// Подпись поста объявления для канала (parse_mode=HTML). Пользовательский текст экранируется,
/// длина держится с запасом ниже лимита подписи Telegram.
/// </summary>
public static class ChannelPostFormatter
{
    /// <summary>Лимит подписи sendPhoto (символов после разбора разметки).</summary>
    public const int MaxCaptionLength = 1024;

    /// <summary>
    /// Бюджет видимого текста подписи. Запас ниже <see cref="MaxCaptionLength"/>: точную методику
    /// подсчёта (эмодзи, суррогатные пары) Telegram не документирует, а упереться в лимит —
    /// это ответ 400 и Failed без повтора.
    /// </summary>
    public const int CaptionBudget = 960;

    public const string ButtonText = "Смотреть на площадке →";

    // Заголовок ограничен CHECK-констрейнтом БД (120), здесь — страховка для бюджета подписи.
    private const int MaxTitleLength = 200;

    private const string Nbsp = " ";

    private static readonly NumberFormatInfo PriceFormat = new()
    {
        NumberGroupSeparator = Nbsp,
        NumberGroupSizes = [3]
    };

    /// <summary>
    /// Полная подпись:
    /// <code>
    /// 📦 #объявления #Город
    ///
    /// Заголовок
    ///
    /// 💰 Цена
    /// 📍 Город
    ///
    /// Описание…
    /// </code>
    /// </summary>
    public static string BuildCaption(ChannelPostContent post, int descriptionMaxLength)
    {
        var city = CatalogLabels.City(post.City);
        var title = Shorten(Clean(post.Title), MaxTitleLength);

        var head = $"📦 #объявления #{Hashtag(city)}\n\n{title}\n\n💰 {FormatPrice(post.PriceType, post.Price)}\n📍 {city}";

        // Длину считаем по исходному тексту, до экранирования: Telegram считает символы после
        // разбора разметки, «&amp;» для него один символ. Резать тоже до экранирования —
        // иначе можно разрезать сущность пополам. «\n\n» перед описанием и «…» — в бюджете.
        var room = CaptionBudget - head.Length - 2 - 1;
        var description = Shorten(Clean(post.Description), Math.Min(descriptionMaxLength, room));

        return description.Length == 0
            ? EscapeHtml(head)
            : $"{EscapeHtml(head)}\n\n{EscapeHtml(description)}";
    }

    /// <summary>Подпись проданного: заголовок и пометка, без описания и цены.</summary>
    public static string BuildSoldCaption(string title) =>
        $"{EscapeHtml(Shorten(Clean(title), MaxTitleLength))}\n\n✅ ПРОДАНО";

    /// <summary>Подпись снятого с публикации (архив, отказ модератора, удаление).</summary>
    public static string BuildRemovedCaption(string title) =>
        $"{EscapeHtml(Shorten(Clean(title), MaxTitleLength))}\n\n⛔ Снято с публикации";

    /// <summary>«1 200 руб.» (неразрывные пробелы) / «Договорная» / «Бесплатно».</summary>
    public static string FormatPrice(PriceType priceType, decimal? price) => priceType switch
    {
        PriceType.Free => "Бесплатно",
        PriceType.Negotiable => "Договорная",
        // Fixed без цены запрещён CHECK-констрейнтом; если всё же пришёл — не печатаем «руб.» без числа.
        _ when price is null => "Договорная",
        _ => $"{decimal.Truncate(price.Value).ToString("#,0", PriceFormat)}{Nbsp}руб."
    };

    /// <summary>Хештег без пробелов и знаков: «Тирасполь» → «Тирасполь», «Новые Анены» → «НовыеАнены».</summary>
    public static string Hashtag(string text) =>
        string.Concat(text.Where(c => char.IsLetterOrDigit(c) || c == '_'));

    /// <summary>
    /// Экранирование для parse_mode=HTML. По документации Bot API заменять нужно ровно
    /// <c>&lt;</c>, <c>&gt;</c> и <c>&amp;</c>; кавычки вне атрибутов разметкой не являются.
    /// </summary>
    public static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Контакты из текста — вон (телефоны, ссылки, ники, почта), пробелы по краям — тоже.
    private static string Clean(string? text) =>
        ListingContentRisk.RedactContacts(text ?? string.Empty).Trim();

    private static string Shorten(string text, int limit)
    {
        if (text.Length <= limit)
            return text;
        if (limit <= 0)
            return string.Empty;

        // Не разрезаем суррогатную пару (эмодзи) пополам.
        var end = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
        return text[..end].TrimEnd() + "…";
    }
}
