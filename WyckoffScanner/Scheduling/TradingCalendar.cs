using System.Globalization;
using Microsoft.Extensions.Options;
using WyckoffScanner.Configuration;

namespace WyckoffScanner.Scheduling;

/// <summary>Mon-Fri (minus configured market holidays) calendar + start-time maths in the configured time zone.</summary>
public sealed class TradingCalendar
{
    private readonly HashSet<DateOnly> _holidays;

    public TimeOnly StartTime { get; }
    public TimeZoneInfo TimeZone { get; }

    public TradingCalendar(IOptions<ScheduleOptions> options)
    {
        var o = options.Value;
        StartTime = TimeOnly.ParseExact(o.StartTime, "HH:mm", CultureInfo.InvariantCulture);
        TimeZone = string.IsNullOrWhiteSpace(o.TimeZoneId) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(o.TimeZoneId);
        _holidays = o.Holidays
            .Select(h => DateOnly.ParseExact(h, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToHashSet();
    }

    public DateTime NowLocal() => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZone);

    public bool IsTradingDay(DateOnly date) =>
        date.DayOfWeek != DayOfWeek.Saturday &&
        date.DayOfWeek != DayOfWeek.Sunday &&
        !_holidays.Contains(date);

    public DateOnly PreviousTradingDay(DateOnly date)
    {
        var d = date.AddDays(-1);
        while (!IsTradingDay(d)) d = d.AddDays(-1);
        return d;
    }

    /// <summary>Next moment (local clock) at which a pipeline run is due.</summary>
    public DateTime NextRunTimeLocal(DateTime nowLocal)
    {
        var today = DateOnly.FromDateTime(nowLocal);
        var todayStart = today.ToDateTime(StartTime);
        if (IsTradingDay(today) && nowLocal < todayStart) return todayStart;

        var d = today.AddDays(1);
        while (!IsTradingDay(d)) d = d.AddDays(1);
        return d.ToDateTime(StartTime);
    }
}
