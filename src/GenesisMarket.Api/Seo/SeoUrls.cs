using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Seo;

/// <summary>
/// Единая точка построения публичных (индексируемых) URL сайта. Пути обязаны совпадать с
/// маршрутизацией фронтенда (Next.js App Router, каталог <c>src/app</c>): карточка объявления —
/// <c>/listing/{slug}</c>, каталог — <c>/catalog</c>, фильтры каталога — query-параметры
/// (<c>?category=Electronics</c>, <c>?cities=Tiraspol</c>). Значения категорий/городов в
/// query-строке — имена enum как есть (PascalCase): фронтенд сверяет их со своим справочником
/// по точному совпадению. Все методы возвращают абсолютные ссылки от базового адреса.
/// </summary>
public static class SeoUrls
{
    /// <summary>Канонический адрес карточки объявления: <c>{base}/listing/{slug}</c>.</summary>
    public static string Listing(string baseUrl, string slug) => $"{baseUrl}/listing/{slug}";

    /// <summary>Главная страница.</summary>
    public static string Home(string baseUrl) => $"{baseUrl}/";

    /// <summary>Каталог (витрина всех объявлений): <c>{base}/catalog</c>.</summary>
    public static string Catalog(string baseUrl) => $"{baseUrl}/catalog";

    /// <summary>Каталог с фильтром по категории: <c>{base}/catalog?category=Transport</c>.</summary>
    public static string CatalogCategory(string baseUrl, Category category) =>
        $"{baseUrl}/catalog?category={category}";

    /// <summary>Каталог с фильтром по городу: <c>{base}/catalog?cities=Tiraspol</c>.</summary>
    public static string CatalogCity(string baseUrl, City city) =>
        $"{baseUrl}/catalog?cities={city}";

    /// <summary>
    /// Статические информационные страницы фронтенда (существующие сегменты <c>src/app</c>).
    /// Приватные (<c>/profile</c>, <c>/favorites</c>, <c>/login</c>, <c>/listing/new</c>,
    /// <c>/moderation</c>, <c>/verify-email</c>) сюда не попадают никогда — см.
    /// <see cref="DisallowedPaths"/>.
    /// </summary>
    public static readonly string[] InfoPaths =
    [
        "/categories", "/about", "/how-it-works", "/help", "/safety", "/contacts"
    ];

    /// <summary>
    /// Пути, закрытые от индексации в robots.txt: личный кабинет, авторизация, создание и
    /// редактирование объявлений, модерация, профили пользователей. Плюс зарезервированные
    /// <c>/create</c>, <c>/auth</c>, <c>/admin</c> — маршрутов с такими именами сейчас нет,
    /// но они закрыты заранее, чтобы появление раздела не открыло его краулеру.
    /// </summary>
    public static readonly string[] DisallowedPaths =
    [
        "/create", "/profile", "/auth", "/admin",
        "/listing/new", "/favorites", "/login", "/moderation", "/verify-email", "/user/"
    ];

    /// <summary>Витрина категории — посадочная «категория × город» (пока только API-сводка).</summary>
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
