using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace GenesisMarket.Api.Moderation;

/// <summary>
/// Кодек курсора очереди модерации. Порядок сортировки — (priority DESC, createdAt ASC,
/// id ASC): сначала автофлаги (высокий приоритет), затем самые старые записи. Курсор
/// хранит именно этот кортеж, чтобы keyset-пагинация была строго детерминирована при
/// равных приоритетах и одинаковых датах.
/// </summary>
public static class ModerationCursor
{
    private const char Separator = '|';

    public static string Encode(int priority, DateTimeOffset createdAt, Guid id)
    {
        var raw = string.Join(Separator,
            priority.ToString(CultureInfo.InvariantCulture),
            createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            id.ToString("N"));
        return WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
    }

    /// <summary>
    /// Разбирает курсор. false при любой некорректности (битая base64, неверный формат) —
    /// вызывающий отвечает 400.
    /// </summary>
    public static bool TryDecode(string cursor, out int priority, out DateTimeOffset createdAt, out Guid id)
    {
        priority = 0;
        createdAt = default;
        id = default;
        try
        {
            var raw = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));
            var parts = raw.Split(Separator);
            if (parts.Length != 3)
                return false;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out priority))
                return false;
            if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return false;
            if (!Guid.TryParseExact(parts[2], "N", out id))
                return false;
            createdAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
