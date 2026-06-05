using SuavoAgent.Watchdog;
using Xunit;

namespace SuavoAgent.Watchdog.Tests;

/// <summary>
/// Pins the alive-but-hung detector. The dangerous miss is a deadlocked process the SCM still
/// reports RUNNING; the dangerous false-positive is restarting a slow starter mid-init. Both
/// boundaries are asserted.
/// </summary>
public sealed class HangEvaluatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(60);

    private static LivenessSnapshot Snap(
        ServiceState scm, DateTimeOffset? beacon, DateTimeOffset now, DateTimeOffset? trackingSince) =>
        new(scm, beacon, now, Threshold, trackingSince);

    [Theory]
    [InlineData(ServiceState.Stopped)]
    [InlineData(ServiceState.StartPending)]
    [InlineData(ServiceState.NotInstalled)]
    [InlineData(ServiceState.Unknown)]
    public void NotRunning_DefersToScmSupervision(ServiceState scm)
    {
        // Hang detection only applies to a RUNNING process; everything else is the SCM engine's job.
        var v = HangEvaluator.Evaluate(Snap(scm, beacon: T0, now: T0.AddHours(1), trackingSince: T0));
        Assert.Equal(LivenessVerdict.NotRunning, v);
    }

    [Fact]
    public void Running_FreshBeacon_IsLive()
    {
        var v = HangEvaluator.Evaluate(Snap(ServiceState.Running, beacon: T0.AddSeconds(30), now: T0.AddSeconds(50), trackingSince: T0));
        Assert.Equal(LivenessVerdict.Live, v);
    }

    [Fact]
    public void Running_StaleBeacon_IsHung()
    {
        // Beacon last refreshed 90s ago, threshold 60s → deadlocked while SCM still says RUNNING.
        var v = HangEvaluator.Evaluate(Snap(ServiceState.Running, beacon: T0, now: T0.AddSeconds(90), trackingSince: T0));
        Assert.Equal(LivenessVerdict.Hung, v);
    }

    [Fact]
    public void Running_NoBeacon_WithinStartupGrace_IsBeaconPending()
    {
        // Tracking for 40s (< 60s threshold), no beacon yet → a slow starter, NOT a hang.
        var v = HangEvaluator.Evaluate(Snap(ServiceState.Running, beacon: null, now: T0.AddSeconds(40), trackingSince: T0));
        Assert.Equal(LivenessVerdict.BeaconPending, v);
    }

    [Fact]
    public void Running_NoBeacon_PastStartupGrace_IsHung()
    {
        // Tracking for 90s with never a beacon → up but never serving → hung.
        var v = HangEvaluator.Evaluate(Snap(ServiceState.Running, beacon: null, now: T0.AddSeconds(90), trackingSince: T0));
        Assert.Equal(LivenessVerdict.Hung, v);
    }

    [Fact]
    public void Running_NoBeacon_NoTrackingStartYet_IsBeaconPending()
    {
        // First tick we ever saw it RUNNING (tracking not yet started) → give it the grace window.
        var v = HangEvaluator.Evaluate(Snap(ServiceState.Running, beacon: null, now: T0, trackingSince: null));
        Assert.Equal(LivenessVerdict.BeaconPending, v);
    }

    [Theory]
    [InlineData(60, LivenessVerdict.Live)]   // exactly at threshold → still live (strictly-greater trips)
    [InlineData(61, LivenessVerdict.Hung)]   // one second past → hung
    public void BeaconAge_TripsStrictlyPastThreshold(int ageSeconds, LivenessVerdict expected)
    {
        var v = HangEvaluator.Evaluate(Snap(ServiceState.Running, beacon: T0, now: T0.AddSeconds(ageSeconds), trackingSince: T0));
        Assert.Equal(expected, v);
    }
}
