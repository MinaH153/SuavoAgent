namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Pure scheduling math for the autonomous daily pricing run. Separated from the worker so the
/// next-run logic is unit-testable without timers or a host.
/// </summary>
public static class PricingScheduleCalculator
{
    /// <summary>
    /// The next instant at which the local wall-clock reads <paramref name="runAt"/>, at or after
    /// <paramref name="now"/>: today at that local time if it is still in the future, otherwise the
    /// next day it occurs.
    ///
    /// <para>Computed in <paramref name="tz"/>'s wall clock — NOT from <paramref name="now"/>'s current
    /// offset — so "run at 02:00" stays at 02:00 every day even across a DST transition (when the UTC
    /// offset itself shifts). Building tomorrow from today's offset drifts the firing time by the DST
    /// delta on transition days and can even schedule a second same-day run. A spring-forward gap (the
    /// wall-clock time does not exist that day) is skipped to the next day it is valid; an ambiguous
    /// fall-back time resolves deterministically to the zone's standard offset.</para>
    /// </summary>
    public static DateTimeOffset NextRun(DateTimeOffset now, TimeOnly runAt, TimeZoneInfo tz)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(now, tz);
        var startDate = DateOnly.FromDateTime(nowLocal.DateTime);

        // Bounded scan: today, then up to a few days out to step over a spring-forward gap.
        for (var addDays = 0; addDays <= 3; addDays++)
        {
            var wall = startDate.AddDays(addDays).ToDateTime(runAt); // DateTimeKind.Unspecified
            if (tz.IsInvalidTime(wall)) continue; // the clock skips this instant — try the next day
            var candidate = new DateTimeOffset(wall, tz.GetUtcOffset(wall));
            if (candidate > now) return candidate;
        }

        // Unreachable in practice (a gap never spans multiple days); fail safe to ~one day out.
        return now.AddDays(1);
    }

    /// <summary>
    /// Parse an "HH:mm" 24-hour local time. Returns false (and TimeOnly.MinValue) on anything
    /// malformed — the worker then logs and stays idle rather than running at an unintended time.
    /// </summary>
    public static bool TryParseRunAt(string? value, out TimeOnly runAt)
    {
        runAt = default;
        return !string.IsNullOrWhiteSpace(value)
            && TimeOnly.TryParseExact(value.Trim(), "HH:mm", out runAt);
    }
}
