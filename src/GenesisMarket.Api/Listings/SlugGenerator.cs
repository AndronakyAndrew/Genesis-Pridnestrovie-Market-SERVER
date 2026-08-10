using System.Text;
using System.Text.RegularExpressions;

namespace GenesisMarket.Api.Listings;

/// <summary>
/// Генерация ЧПУ-slug: транслитерация кириллицы в латиницу + короткий хеш.
/// Уникальность гарантируется уникальным индексом БД; при коллизии вызывающая
/// сторона перегенерирует с новым суффиксом (<see cref="RandomSuffix"/>).
/// </summary>
public static partial class SlugGenerator
{
    private static readonly Dictionary<char, string> Map = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e",
        ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k",
        ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r",
        ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya"
    };

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugChars();

    /// <summary>slug из заголовка + суффикса (короткий хеш Id или случайный).</summary>
    public static string Generate(string title, string suffix)
    {
        var sb = new StringBuilder(title.Length);
        foreach (var ch in title.ToLowerInvariant())
            sb.Append(Map.TryGetValue(ch, out var rep) ? rep : ch);

        var slug = NonSlugChars().Replace(sb.ToString(), "-").Trim('-');
        if (slug.Length > 80)
            slug = slug[..80].Trim('-');
        if (slug.Length == 0)
            slug = "obyavlenie";

        return $"{slug}-{suffix}";
    }

    /// <summary>Короткий детерминированный суффикс из Id (первые 8 hex-символов).</summary>
    public static string SuffixFromId(Guid id) => id.ToString("N")[..8];

    /// <summary>Случайный суффикс — для повторной генерации при коллизии slug.</summary>
    public static string RandomSuffix() => Guid.CreateVersion7().ToString("N")[..8];
}
