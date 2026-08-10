using System.Text.RegularExpressions;

namespace GenesisMarket.Api.Auth;

/// <summary>Настройки телефона. Секция <c>Phone</c>.</summary>
public sealed class PhoneOptions
{
    public const string Section = "Phone";

    /// <summary>
    /// Разрешить коды стран, отличные от +373/+7. По умолчанию false —
    /// принимаем только ПМР/Молдову (+373) и частый в регионе +7.
    /// </summary>
    public bool AllowOtherCountries { get; set; }
}

/// <summary>
/// Приведение телефона к E.164. Пользователь может вводить в международном
/// формате (<c>+373…</c> / <c>+7…</c>) или локально с ведущим нулём
/// (<c>0 775-12-345</c>) — тогда <c>0</c> заменяется на код <c>+373</c>.
/// Прочие коды стран — только если явно разрешено флагом конфигурации.
/// В БД номер всегда хранится в E.164.
/// </summary>
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^\+(373|7)\d{6,12}$")]
    private static partial Regex LocalCodes();

    [GeneratedRegex(@"^\+\d{7,15}$")]
    private static partial Regex AnyCode();

    [GeneratedRegex(@"[\s\-()]")]
    private static partial Regex Separators();

    /// <summary>Возвращает нормализованный E.164 или null, если номер некорректен.</summary>
    public static string? Normalize(string? input, bool allowOtherCountries = false)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var cleaned = Separators().Replace(input, "");

        // Локальный ввод с ведущим нулём: 0XXXXXXXX -> +373XXXXXXXX.
        var candidate = cleaned.StartsWith('0')
            ? "+373" + cleaned[1..]
            : cleaned;

        var valid = allowOtherCountries
            ? AnyCode().IsMatch(candidate)
            : LocalCodes().IsMatch(candidate);

        return valid ? candidate : null;
    }
}
