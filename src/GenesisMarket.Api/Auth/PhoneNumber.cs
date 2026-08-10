using System.Text.RegularExpressions;

namespace GenesisMarket.Api.Auth;

/// <summary>
/// Приведение телефона к E.164. Пользователь может вводить как в международном
/// формате (<c>+373…</c> / <c>+7…</c>), так и локально с ведущим нулём
/// (<c>0 775-12-345</c>) — тогда <c>0</c> заменяется на код ПМР/Молдовы <c>+373</c>.
/// В БД номер всегда хранится в E.164.
/// </summary>
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^\+(373|7)\d{6,12}$")]
    private static partial Regex E164();

    [GeneratedRegex(@"[\s\-()]")]
    private static partial Regex Separators();

    /// <summary>Возвращает нормализованный E.164 или null, если номер некорректен.</summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        // Убираем пробелы, скобки, дефисы.
        var cleaned = Separators().Replace(input, "");

        // Локальный ввод с ведущим нулём: 0XXXXXXXX -> +373XXXXXXXX.
        var candidate = cleaned.StartsWith('0')
            ? "+373" + cleaned[1..]
            : cleaned;

        return E164().IsMatch(candidate) ? candidate : null;
    }
}
