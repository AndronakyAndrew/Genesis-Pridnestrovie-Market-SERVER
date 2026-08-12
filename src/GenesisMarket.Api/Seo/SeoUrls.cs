using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Seo;

/// <summary>
/// Единая точка построения публичных (индексируемых) URL сайта. Пути должны совпадать с
/// маршрутизацией фронтенда: карточка объявления — <c>/obyavlenie/{slug}</c>, посадочные —
/// <c>/{category}/{city}</c>. Все методы возвращают абсолютные ссылки от базового адреса.
/// Значения категорий/городов в путях — те же строковые метки, что и в БД (<c>realestate</c>,
/// <c>tiraspol</c>): человекочитаемо и стабильно.
/// </summary>
public static class SeoUrls
{
    /// <summary>Канонический адрес карточки объявления: <c>{base}/obyavlenie/{slug}</c>.</summary>
    public static string Listing(string baseUrl, string slug) => $"{baseUrl}/obyavlenie/{slug}";

    /// <summary>Главная страница.</summary>
    public static string Home(string baseUrl) => $"{baseUrl}/";

    /// <summary>Витрина категории: <c>{base}/{category}</c>.</summary>
    public static string Category(string baseUrl, Category category) =>
        $"{baseUrl}/{Value(category)}";

    /// <summary>Витрина города: <c>{base}/city/{city}</c>.</summary>
    public static string City(string baseUrl, City city) =>
        $"{baseUrl}/city/{Value(city)}";

    /// <summary>Посадочная «категория × город»: <c>{base}/{category}/{city}</c>.</summary>
    public static string Landing(string baseUrl, Category category, City city) =>
        $"{baseUrl}/{Value(category)}/{Value(city)}";

    public static string Sitemap(string baseUrl) => $"{baseUrl}/sitemap.xml";

    /// <summary>Строковая метка enum как в БД (RealEstate → «realestate»).</summary>
    public static string Value<TEnum>(TEnum value) where TEnum : struct, Enum =>
        value.ToString().ToLowerInvariant();

    /// <summary>
    /// Разбор строковой метки enum из маршрута (регистронезависимо). Используется
    /// посадочными и sitemap-путями, где категория/город приходят строкой из URL.
    /// </summary>
    public static bool TryParse<TEnum>(string? value, out TEnum parsed) where TEnum : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
}
