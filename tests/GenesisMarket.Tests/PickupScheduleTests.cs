using GenesisMarket.Api.Business;
using GenesisMarket.Api.Contracts;
using Xunit;

namespace GenesisMarket.Tests;

public sealed class PickupScheduleTests
{
    [Fact]
    public void Evaluates_open_closed_break_and_missing_schedule_using_explicit_instants()
    {
        Assert.Equal(TimeSpan.FromHours(2), PickupSchedule.TimeZone.GetUtcOffset(new DateTime(2026, 1, 5)));

        var openHours = WeekdayHours();
        var open = PickupSchedule.Evaluate(openHours, AtLocal(2026, 1, 5, 10, 0));
        Assert.True(open.OpenNow);
        Assert.Equal("18:00", open.ClosesAt);
        Assert.Null(open.OpensAt);

        var closedMonday = openHours with
        {
            Days = openHours.Days!.Select(x => x.Day == 1
                ? x with { Closed = true, From = null, To = null }
                : x).ToList()
        };
        var closed = PickupSchedule.Evaluate(closedMonday, AtLocal(2026, 1, 5, 10, 0));
        Assert.False(closed.OpenNow);
        Assert.Equal("09:00", closed.OpensAt);

        var withBreak = openHours with { BreakFrom = "13:00", BreakTo = "14:00" };
        var breakState = PickupSchedule.Evaluate(withBreak, AtLocal(2026, 1, 5, 13, 30));
        Assert.False(breakState.OpenNow);
        Assert.Equal("14:00", breakState.OpensAt);

        var noHours = PickupSchedule.Evaluate(null, AtLocal(2026, 1, 5, 10, 0));
        Assert.Null(noHours.OpenNow);
        Assert.Null(noHours.ClosesAt);
        Assert.Null(noHours.OpensAt);
    }

    [Fact]
    public void Validates_seven_unique_days_ranges_and_break_for_every_open_day()
    {
        Assert.True(PickupSchedule.TryNormalize(WeekdayHours(), out var normalized, out _));
        Assert.Equal(7, normalized!.Days!.Count);

        Assert.False(PickupSchedule.TryNormalize(WeekdayHours() with
        {
            Days = WeekdayHours().Days!.Take(6).ToList()
        }, out _, out var missingDayError));
        Assert.Equal("invalid_pickup_hours", missingDayError);

        Assert.False(PickupSchedule.TryNormalize(WeekdayHours() with
        {
            Days = WeekdayHours().Days!.Select((x, i) => i == 6 ? x with { Day = 1 } : x).ToList()
        }, out _, out var duplicateDayError));
        Assert.Equal("invalid_pickup_hours", duplicateDayError);

        Assert.False(PickupSchedule.TryNormalize(WeekdayHours() with
        {
            Days = WeekdayHours().Days!.Select(x => x.Day == 1 ? x with { From = "18:00", To = "09:00" } : x).ToList()
        }, out _, out var rangeError));
        Assert.Equal("invalid_pickup_hours", rangeError);

        Assert.False(PickupSchedule.TryNormalize(WeekdayHours() with
        {
            Days = WeekdayHours().Days!.Select(x => x.Day == 2 ? x with { From = "13:00" } : x).ToList(),
            BreakFrom = "12:00",
            BreakTo = "14:00"
        }, out _, out var breakError));
        Assert.Equal("invalid_pickup_break", breakError);
    }

    private static PickupHoursDto WeekdayHours() => new(
        Enumerable.Range(1, 7).Select(day => new PickupDayHoursDto(day, false, "09:00", "18:00")).ToList(),
        null,
        null);

    private static DateTimeOffset AtLocal(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, PickupSchedule.TimeZone);
    }
}
