using System.Diagnostics.CodeAnalysis;

namespace GenesisMarket.Api.Telegram.Channel;

/// <summary>Рабочее окно публикации в канал: местное время против UTC сервера.</summary>
public static class ChannelSchedule
{
    // Проект собран с InvariantGlobalization: на Windows .NET сопоставляет IANA-зоны с
    // Windows-зонами через ICU, а она в этом режиме отключена, — Europe/Chisinau не находится
    // (проверено). На Linux зоны читаются из tzdata напрямую. Запасное имя — только для
    // локального запуска на Windows.
    private static readonly Dictionary<string, string> WindowsFallback = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/Chisinau"] = "E. Europe Standard Time"
    };

    /// <summary>Часовой пояс по идентификатору или null, если его нет в системе.</summary>
    public static TimeZoneInfo? ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        if (TryFind(id, out var zone))
            return zone;

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && TryFind(windowsId, out zone))
            return zone;

        return WindowsFallback.TryGetValue(id, out var fallback) && TryFind(fallback, out zone) ? zone : null;
    }

    /// <summary>
    /// Попадает ли момент <paramref name="utcNow"/> в окно [start; end) по местному времени.
    /// start &gt; end — окно через полночь; start == end — окно пустое.
    /// </summary>
    public static bool IsWithinWindow(DateTimeOffset utcNow, TimeZoneInfo zone, TimeOnly start, TimeOnly end)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);

        return start <= end
            ? local >= start && local < end
            : local >= start || local < end;
    }

    private static bool TryFind(string id, [NotNullWhen(true)] out TimeZoneInfo? zone)
    {
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = null;
            return false;
        }
    }
}
