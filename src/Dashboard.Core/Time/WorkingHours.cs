using Dashboard.Core.Configuration;

namespace Dashboard.Core.Time;

/// <summary>
/// Working-hours arithmetic in the configured IANA time zone. DST-aware: each local day is
/// converted back to UTC independently, so a 09:00–17:30 day is 8.5h on both sides of a change.
/// </summary>
public sealed class WorkingHours
{
    private readonly TimeZoneInfo _tz;
    private readonly TimeOnly _start;
    private readonly TimeOnly _end;
    private readonly HashSet<DayOfWeek> _days;

    public WorkingHours(TimeConfig config)
    {
        _tz = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZone);
        _start = TimeOnly.Parse(config.WorkdayStart);
        _end = TimeOnly.Parse(config.WorkdayEnd);
        _days = [.. config.WorkDays];
    }

    public TimeZoneInfo TimeZone => _tz;
    public double HoursPerDay => (_end - _start).TotalHours;

    public DateTimeOffset ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTime(utc, _tz);

    /// <summary>Working hours elapsed between two instants (0 if to &lt;= from).</summary>
    public double Between(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return 0;
        var localFrom = DateOnly.FromDateTime(ToLocal(from).DateTime);
        var localTo = DateOnly.FromDateTime(ToLocal(to).DateTime);
        double total = 0;
        for (var d = localFrom; d <= localTo; d = d.AddDays(1))
        {
            if (!_days.Contains(d.DayOfWeek)) continue;
            var (ws, we) = DayWindowUtc(d);
            var s = from > ws ? from : ws;
            var e = to < we ? to : we;
            if (e > s) total += (e - s).TotalHours;
        }
        return total;
    }

    public double WorkingDaysBetween(DateTimeOffset from, DateTimeOffset to) => Between(from, to) / HoursPerDay;

    private (DateTimeOffset Start, DateTimeOffset End) DayWindowUtc(DateOnly day)
    {
        var s = day.ToDateTime(_start, DateTimeKind.Unspecified);
        var e = day.ToDateTime(_end, DateTimeKind.Unspecified);
        return (ToUtc(s), ToUtc(e));
    }

    private DateTimeOffset ToUtc(DateTime local)
    {
        // Skip invalid (spring-forward) times by nudging forward; ambiguous times take the standard offset.
        while (_tz.IsInvalidTime(local)) local = local.AddMinutes(30);
        var offset = _tz.IsAmbiguousTime(local) ? _tz.GetAmbiguousTimeOffsets(local).Min() : _tz.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>Next occurrence of the given local hour strictly after <paramref name="after"/>.</summary>
    public DateTimeOffset NextLocalHour(DateTimeOffset after, int hour)
    {
        var local = ToLocal(after);
        var candidate = ToUtc(local.Date.AddHours(hour));
        return candidate > after ? candidate : ToUtc(local.Date.AddDays(1).AddHours(hour));
    }

    /// <summary>Most recent occurrence of the given local hour at or before <paramref name="now"/>.</summary>
    public DateTimeOffset PreviousLocalHour(DateTimeOffset now, int hour)
    {
        var local = ToLocal(now);
        var candidate = ToUtc(local.Date.AddHours(hour));
        return candidate <= now ? candidate : ToUtc(local.Date.AddDays(-1).AddHours(hour));
    }

    public DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(ToLocal(utc).DateTime);
}
