using GenesisMarket.Domain.Enums;

namespace GenesisMarket.Api.Moderation;

/// <summary>Разбор строки поиска на экранах модератора.</summary>
public static class ModerationSearch
{
    /// <summary>Символ экранирования для ILIKE-шаблонов (<see cref="LikePattern"/>).</summary>
    public const string LikeEscape = "\\";

    /// <summary>Максимальная длина строки поиска — длиннее обрезаем, а не ищем по мусору.</summary>
    public const int MaxLength = 100;

    /// <summary>«ID профиля»: ровно 5 цифр (допускается префикс GEN- и #, как в интерфейсе).</summary>
    public static bool TryPublicCode(string q, out string code)
    {
        var s = q.Trim();
        if (s.StartsWith("GEN-", StringComparison.OrdinalIgnoreCase))
            s = s[4..];
        s = s.TrimStart('#');
        code = s;
        return s.Length == 5 && s.All(char.IsAsciiDigit);
    }

    /// <summary>Подстрочный ILIKE-шаблон с экранированием % _ \ (иначе «100%» искало бы всё).</summary>
    public static string LikePattern(string q) =>
        "%" + q.Trim()
            .Replace(LikeEscape, LikeEscape + LikeEscape)
            .Replace("%", LikeEscape + "%")
            .Replace("_", LikeEscape + "_") + "%";

    /// <summary>Нормализованная строка поиска или null, если искать нечего.</summary>
    public static string? Normalize(string? q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return null;
        var s = q.Trim();
        return s.Length > MaxLength ? s[..MaxLength] : s;
    }
}

/// <summary>
/// Тяжесть причины жалобы — порядок «сначала срочные» на экране жалоб. Мошенничество
/// и запрещённые товары наносят вред покупателю прямо сейчас; спам и неверная
/// категория подождут.
/// </summary>
public static class ReportSeverity
{
    public const int High = 3;
    public const int Medium = 2;
    public const int Low = 1;

    public static int Of(ReportReason reason) => reason switch
    {
        ReportReason.Fraud or ReportReason.Prohibited => High,
        ReportReason.PriceViolation => Medium,
        _ => Low
    };
}
