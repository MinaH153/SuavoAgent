using System;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>
/// Pure scheduling math for the autonomous daily pricing run (PricingScheduleWorker). No timers,
/// no host — just the next-run computation and the "HH:mm" parse that decides when the bot fires.
/// </summary>
public class PricingScheduleCalculatorTests
{
    private static readonly TimeSpan Pst = TimeSpan.FromHours(-8);
    private static readonly TimeSpan Pdt = TimeSpan.FromHours(-7);

    // A fixed -8 zone with NO DST rules, so the basic next-run math is deterministic and independent
    // of the host's local time zone.
    private static readonly TimeZoneInfo FixedMinus8 =
        TimeZoneInfo.CreateCustomTimeZone("fixed-8", Pst, "Fixed-8", "Fixed-8");

    // The real US Pacific zone (IANA id works on .NET 6+ across macOS/Linux/Windows) for DST cases.
    private static readonly TimeZoneInfo Pacific =
        TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    [Fact]
    public void NextRun_WhenRunTimeStillAheadToday_ReturnsToday()
    {
        var now = new DateTimeOffset(2026, 6, 7, 1, 0, 0, Pst); // 01:00, run at 02:00
        var next = PricingScheduleCalculator.NextRun(now, new TimeOnly(2, 0), FixedMinus8);

        Assert.Equal(new DateTimeOffset(2026, 6, 7, 2, 0, 0, Pst), next);
    }

    [Fact]
    public void NextRun_WhenRunTimeAlreadyPassedToday_ReturnsTomorrow()
    {
        var now = new DateTimeOffset(2026, 6, 7, 3, 0, 0, Pst); // 03:00, run at 02:00 already gone
        var next = PricingScheduleCalculator.NextRun(now, new TimeOnly(2, 0), FixedMinus8);

        Assert.Equal(new DateTimeOffset(2026, 6, 8, 2, 0, 0, Pst), next);
    }

    [Fact]
    public void NextRun_WhenExactlyAtRunTime_ReturnsTomorrow_NoDoubleFire()
    {
        // A run that just finished at exactly 02:00 must not immediately re-fire for the same day.
        var now = new DateTimeOffset(2026, 6, 7, 2, 0, 0, Pst);
        var next = PricingScheduleCalculator.NextRun(now, new TimeOnly(2, 0), FixedMinus8);

        Assert.Equal(new DateTimeOffset(2026, 6, 8, 2, 0, 0, Pst), next);
    }

    [Fact]
    public void NextRun_CrossesMidnightAndYear()
    {
        var now = new DateTimeOffset(2026, 12, 31, 23, 59, 0, Pst); // crosses midnight + year
        var next = PricingScheduleCalculator.NextRun(now, new TimeOnly(2, 0), FixedMinus8);

        Assert.Equal(new DateTimeOffset(2027, 1, 1, 2, 0, 0, Pst), next);
        Assert.Equal(Pst, next.Offset);
    }

    [Fact]
    public void NextRun_AcrossSpringForward_KeepsWallClockTime_NotStaleOffset()
    {
        // Mar 8 2026 is spring-forward (PST -8 → PDT -7 at 02:00). The night before, "the next 22:00"
        // is tomorrow — and tomorrow it is PDT, so the firing instant must carry -7, NOT yesterday's -8.
        // The naive (now.Offset) approach would fire an hour off.
        var now = new DateTimeOffset(2026, 3, 7, 23, 0, 0, Pst); // PST the evening before
        var next = PricingScheduleCalculator.NextRun(now, new TimeOnly(22, 0), Pacific);

        Assert.Equal(2026, next.Year);
        Assert.Equal(3, next.Month);
        Assert.Equal(8, next.Day);
        Assert.Equal(22, next.Hour);
        Assert.Equal(Pdt, next.Offset); // correct post-transition offset
    }

    [Fact]
    public void NextRun_WhenRunTimeFallsInSpringForwardGap_SkipsToNextValidDay()
    {
        // 02:30 does NOT exist on Mar 8 2026 (the clock jumps 02:00 → 03:00). The run must skip the
        // non-existent instant rather than fire at a wrong time — next valid 02:30 is Mar 9 (PDT).
        var now = new DateTimeOffset(2026, 3, 8, 0, 30, 0, Pst);
        var next = PricingScheduleCalculator.NextRun(now, new TimeOnly(2, 30), Pacific);

        Assert.Equal(new DateTimeOffset(2026, 3, 9, 2, 30, 0, Pdt), next);
    }

    [Fact]
    public void NextRun_WhenRunTimeIsAmbiguousFallBack_ResolvesDeterministicallyToStandard()
    {
        // Nov 1 2026 falls back (PDT -7 → PST -8). 01:30 occurs twice; resolving to the standard
        // offset (-8) makes the schedule fire exactly once, never twice, on the fall-back night.
        var now = new DateTimeOffset(2026, 11, 1, 0, 30, 0, Pdt);
        var next = PricingScheduleCalculator.NextRun(now, new TimeOnly(1, 30), Pacific);

        Assert.Equal(1, next.Day);
        Assert.Equal(11, next.Month);
        Assert.Equal(1, next.Hour);
        Assert.Equal(30, next.Minute);
        Assert.Equal(Pst, next.Offset); // standard offset chosen deterministically
    }

    [Theory]
    [InlineData("02:00", 2, 0)]
    [InlineData("00:00", 0, 0)]
    [InlineData("23:59", 23, 59)]
    [InlineData("14:30", 14, 30)]
    [InlineData(" 02:00 ", 2, 0)] // trimmed
    public void TryParseRunAt_ValidTimes_Parse(string value, int hour, int minute)
    {
        Assert.True(PricingScheduleCalculator.TryParseRunAt(value, out var runAt));
        Assert.Equal(new TimeOnly(hour, minute), runAt);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("24:00")]  // hour out of range
    [InlineData("23:60")]  // minute out of range
    [InlineData("2pm")]
    [InlineData("0200")]   // no separator
    [InlineData("not a time")]
    public void TryParseRunAt_InvalidTimes_FailClosed(string? value)
    {
        Assert.False(PricingScheduleCalculator.TryParseRunAt(value, out var runAt));
        Assert.Equal(default, runAt);
    }
}
