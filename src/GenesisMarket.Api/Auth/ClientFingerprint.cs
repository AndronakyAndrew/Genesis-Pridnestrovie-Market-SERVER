using System.Net;

namespace GenesisMarket.Api.Auth;

/// <summary>Разобранный User-Agent: только семейства, без версий.</summary>
public sealed record ClientAgent(string? Device, string? Browser, string? Os);

/// <summary>
/// Приведение User-Agent и IP к тому минимуму, который показывается владельцу
/// в списке сессий.
///
/// Разбор сознательно грубый и написан руками, без внешней библиотеки. Точный
/// парсер (UAParser и подобные) выдаёт версии сборок и модели устройств — это
/// более узкий отпечаток, чем нужно для строки «Chrome, Windows, Компьютер»,
/// и он же сам по себе становится данными о пользователе. Плюс это ещё одна
/// зависимость в проекте, где образы пинятся по digest, а уязвимые пакеты
/// роняют CI. Неизвестное семейство — null, а не «Other»: пусто честнее догадки.
/// </summary>
public static class ClientFingerprint
{
    /// <summary>Максимум символов на семейство (ограничение колонок в БД).</summary>
    public const int FamilyMaxLength = 32;

    /// <summary>Максимум символов префикса IP: хватает на два хекстета IPv6.</summary>
    public const int IpPrefixMaxLength = 39;

    public static ClientAgent ParseUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return new ClientAgent(null, null, null);

        var ua = userAgent;

        return new ClientAgent(Device(ua), Browser(ua), Os(ua));
    }

    /// <summary>
    /// Первые два октета IPv4 («185.112.4.9» → «185.112») либо два хекстета IPv6
    /// («2a02:6b8:c02::1» → «2a02:6b8»). Неразбираемый адрес — null.
    /// </summary>
    public static string? IpPrefix(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var parsed))
            return null;

        // IPv4-mapped IPv6 (::ffff:185.112.4.9) приводим к IPv4: иначе префикс
        // получился бы «0:0» и не значил бы ничего.
        if (parsed.IsIPv4MappedToIPv6)
            parsed = parsed.MapToIPv4();

        var text = parsed.ToString();

        if (parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var octets = text.Split('.');
            return octets.Length >= 2 ? $"{octets[0]}.{octets[1]}" : null;
        }

        var hextets = text.Split(':');
        if (hextets.Length < 2 || hextets[0].Length == 0)
            return null;

        return hextets[1].Length == 0 ? hextets[0] : $"{hextets[0]}:{hextets[1]}";
    }

    // ---- разбор User-Agent ----

    private static string? Device(string ua)
    {
        if (Has(ua, "iPad") || (Has(ua, "Tablet") && !Has(ua, "Mobile")))
            return "Планшет";
        if (Has(ua, "Mobi") || Has(ua, "iPhone") || Has(ua, "Android"))
            return "Телефон";
        if (Has(ua, "Windows") || Has(ua, "Macintosh") || Has(ua, "X11") || Has(ua, "Linux"))
            return "Компьютер";
        return null;
    }

    /// <summary>Порядок проверок важен: почти все подделывают строки друг друга.</summary>
    private static string? Browser(string ua)
    {
        if (Has(ua, "Edg/") || Has(ua, "EdgA/")) return "Edge";      // Edge пишет и Chrome, и Safari
        if (Has(ua, "OPR/") || Has(ua, "Opera")) return "Opera";     // Opera пишет Chrome
        if (Has(ua, "YaBrowser")) return "Яндекс.Браузер";           // тоже пишет Chrome
        if (Has(ua, "SamsungBrowser")) return "Samsung Internet";
        if (Has(ua, "Firefox") || Has(ua, "FxiOS")) return "Firefox";
        if (Has(ua, "CriOS") || Has(ua, "Chrome")) return "Chrome";  // после всех, кто им прикидывается
        if (Has(ua, "Safari")) return "Safari";                      // Safari — последним из браузеров
        return null;
    }

    private static string? Os(string ua)
    {
        if (Has(ua, "Windows")) return "Windows";
        if (Has(ua, "Android")) return "Android";                    // до Linux: Android содержит Linux
        if (Has(ua, "iPhone") || Has(ua, "iPad") || Has(ua, "iOS")) return "iOS";
        if (Has(ua, "Mac OS X") || Has(ua, "Macintosh")) return "macOS";
        if (Has(ua, "Linux") || Has(ua, "X11")) return "Linux";
        return null;
    }

    private static bool Has(string ua, string token) =>
        ua.Contains(token, StringComparison.OrdinalIgnoreCase);
}
