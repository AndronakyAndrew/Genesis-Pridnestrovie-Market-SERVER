using System.Globalization;
using GenesisMarket.Api.Contracts;

namespace GenesisMarket.Api.Business;

public sealed record PickupScheduleState(bool? OpenNow, string? ClosesAt, string? OpensAt);

public static class PickupSchedule
{
    private const string TimeFormat = "HH:mm";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo PridnestrovieTimeZone = ResolveTimeZone();

    public static TimeZoneInfo TimeZone => PridnestrovieTimeZone;

    public static bool TryNormalize(
        PickupHoursDto? input,
        out PickupHoursDto? normalized,
        out string? errorCode)
    {
        normalized = null;
        errorCode = null;
        if (input is null)
            return true;

        if (input.Days is null || input.Days.Count != 7 ||
            input.Days.Any(d => d.Day is < 1 or > 7) ||
            input.Days.Select(d => d.Day).Distinct().Count() != 7)
        {
            errorCode = "invalid_pickup_hours";
            return false;
        }

        var days = new List<PickupDayHoursDto>(7);
        foreach (var day in input.Days.OrderBy(d => d.Day))
        {
            if (day.Closed)
            {
                days.Add(new PickupDayHoursDto(day.Day, true, null, null));
                continue;
            }

            if (!TryTime(day.From, out var from) || !TryTime(day.To, out var to) || from >= to)
            {
                errorCode = "invalid_pickup_hours";
                return false;
            }

            days.Add(new PickupDayHoursDto(day.Day, false, Format(from), Format(to)));
        }

        var breakFromText = string.IsNullOrWhiteSpace(input.BreakFrom) ? null : input.BreakFrom.Trim();
        var breakToText = string.IsNullOrWhiteSpace(input.BreakTo) ? null : input.BreakTo.Trim();
        TimeOnly? breakFrom = null;
        TimeOnly? breakTo = null;
        if ((breakFromText is null) != (breakToText is null))
        {
            errorCode = "invalid_pickup_break";
            return false;
        }

        if (breakFromText is not null)
        {
            if (!TryTime(breakFromText, out var from) || !TryTime(breakToText, out var to) || from >= to)
            {
                errorCode = "invalid_pickup_break";
                return false;
            }

            var openDays = days.Where(d => !d.Closed).ToList();
            if (openDays.Count == 0 || openDays.Any(d =>
                    !TryTime(d.From, out var dayFrom) || !TryTime(d.To, out var dayTo) ||
                    from < dayFrom || to > dayTo))
            {
                errorCode = "invalid_pickup_break";
                return false;
            }

            breakFrom = from;
            breakTo = to;
        }

        normalized = new PickupHoursDto(days, breakFrom is null ? null : Format(breakFrom.Value),
            breakTo is null ? null : Format(breakTo.Value));
        return true;
    }

    public static PickupScheduleState Evaluate(PickupHoursDto? hours, DateTimeOffset now)
    {
        if (hours is null || hours.Days is null)
            return new PickupScheduleState(null, null, null);

        var local = TimeZoneInfo.ConvertTime(now, PridnestrovieTimeZone);
        var today = DayNumber(local.DayOfWeek);
        var nowTime = TimeOnly.FromDateTime(local.DateTime);
        var todaySchedule = hours.Days.FirstOrDefault(d => d.Day == today);
        if (todaySchedule is { Closed: false } && TryTime(todaySchedule.From, out var opens)
            && TryTime(todaySchedule.To, out var closes))
        {
            if (nowTime >= opens && nowTime < closes)
            {
                if (hours.BreakFrom is not null && hours.BreakTo is not null &&
                    TryTime(hours.BreakFrom, out var breakFrom) && TryTime(hours.BreakTo, out var breakTo) &&
                    nowTime >= breakFrom && nowTime < breakTo)
                    return new PickupScheduleState(false, null, Format(breakTo));

                return new PickupScheduleState(true, Format(closes), null);
            }

            if (nowTime < opens)
                return new PickupScheduleState(false, null, Format(opens));
        }

        for (var offset = 1; offset <= 7; offset++)
        {
            var day = ((today - 1 + offset) % 7) + 1;
            var schedule = hours.Days.FirstOrDefault(d => d.Day == day);
            if (schedule is { Closed: false } && TryTime(schedule.From, out var nextOpen))
                return new PickupScheduleState(false, null, Format(nextOpen));
        }

        return new PickupScheduleState(false, null, null);
    }

    private static int DayNumber(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    private static bool TryTime(string? value, out TimeOnly result) =>
        TimeOnly.TryParseExact(value, TimeFormat, Invariant, DateTimeStyles.None, out result);

    private static string Format(TimeOnly value) => value.ToString(TimeFormat, Invariant);

    private static TimeZoneInfo ResolveTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Chisinau");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
        }
    }
}
