using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Health;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

/// <summary>
/// Self-heal is allowed to restart the Helper EXACTLY when the observed live-box strand
/// signature persists (K consecutive conclusive ping-dead verdicts) and the bounds allow —
/// and at no other time. These tests adversarially pin the no-thrash guarantees: no trigger
/// on skips, on a responsive pipe, on a dead Helper (Broker's job), inside the backoff
/// window, or past the 24h cap.
/// </summary>
public class HelperSelfHealTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    private static ActuationReadinessSnapshot Strand(int streak, string? skipped = null, bool responsive = false) =>
        new(
            CommandPipeResponsive: responsive,
            IsConsoleInteractive: false,
            HelperSessionId: null,
            ActiveConsoleSessionId: null,
            HelperPid: null,
            FailureCode: "ping_unanswered",
            FailureReason: "stranded",
            LastConclusiveCheckAtUtc: T0,
            LastProbeAttemptAtUtc: T0,
            SkippedReason: skipped,
            ConsecutiveStrandFailures: streak);

    // ---------------- pure policy ----------------

    [Fact]
    public void BelowStreakThreshold_NoTrigger()
    {
        var d = HelperSelfHealPolicy.Decide(Strand(2), Array.Empty<DateTimeOffset>(), T0);
        Assert.False(d.Trigger);
        Assert.Equal("streak_below_threshold", d.Reason);
    }

    [Fact]
    public void AtStreakThreshold_CleanHistory_Triggers()
    {
        var d = HelperSelfHealPolicy.Decide(Strand(3), Array.Empty<DateTimeOffset>(), T0);
        Assert.True(d.Trigger);
    }

    [Fact]
    public void SkippedProbe_NeverTriggers_EvenWithHighStreak()
    {
        // A skip carries the PREVIOUS verdict — acting on it would restart the Helper based
        // on stale evidence while a job may be using the pipe.
        var d = HelperSelfHealPolicy.Decide(Strand(99, skipped: "pipe_busy"), Array.Empty<DateTimeOffset>(), T0);
        Assert.False(d.Trigger);
        Assert.Equal("probe_skipped", d.Reason);
    }

    [Fact]
    public void ResponsivePipe_NeverTriggers()
    {
        var d = HelperSelfHealPolicy.Decide(Strand(99, responsive: true), Array.Empty<DateTimeOffset>(), T0);
        Assert.False(d.Trigger);
        Assert.Equal("pipe_responsive", d.Reason);
    }

    [Fact]
    public void InsideBackoffWindow_NoTrigger()
    {
        var attempts = new[] { T0.AddMinutes(-5) }; // < 10 min ago
        var d = HelperSelfHealPolicy.Decide(Strand(3), attempts, T0);
        Assert.False(d.Trigger);
        Assert.Equal("backoff", d.Reason);
    }

    [Fact]
    public void PastBackoffWindow_Triggers()
    {
        var attempts = new[] { T0.AddMinutes(-11) };
        var d = HelperSelfHealPolicy.Decide(Strand(3), attempts, T0);
        Assert.True(d.Trigger);
    }

    [Fact]
    public void WindowCapReached_Exhausted()
    {
        var attempts = new[] { T0.AddHours(-20), T0.AddHours(-15), T0.AddHours(-10), T0.AddHours(-5) };
        var d = HelperSelfHealPolicy.Decide(Strand(3), attempts, T0);
        Assert.False(d.Trigger);
        Assert.Equal("exhausted", d.Reason);
    }

    [Fact]
    public void Prune_DropsAttemptsOlderThan24h()
    {
        var pruned = HelperSelfHealPolicy.Prune(
            new[] { T0.AddHours(-25), T0.AddHours(-23) }, T0);
        Assert.Single(pruned);
        Assert.Equal(T0.AddHours(-23), pruned[0]);
    }

    // ---------------- stateful coordinator ----------------

    [Fact]
    public void Coordinator_Triggers_WritesSentinelAndAudit_AndRecordsAttempt()
    {
        var sentinels = new List<HelperRestartRequest.Payload>();
        var audits = new List<string>();
        var c = new HelperSelfHealCoordinator(
            sentinels.Add, audits.Add, NullLogger<HelperSelfHealCoordinator>.Instance);

        var d = c.Consider(Strand(3), T0);

        Assert.True(d.Trigger);
        var sentinel = Assert.Single(sentinels);
        Assert.Equal("self_heal", sentinel.RequestedBy);
        Assert.Equal(T0, sentinel.RequestedAtUtc);
        Assert.Contains("ping_unanswered", sentinel.Reason);
        Assert.Single(audits);

        var state = c.Snapshot();
        Assert.Equal(T0, state.LastAttemptAtUtc);
        Assert.Equal(1, state.AttemptsInWindow);
        Assert.False(state.Exhausted);
    }

    [Fact]
    public void Coordinator_EnforcesBackoff_ThenAllowsAgain()
    {
        var sentinels = new List<HelperRestartRequest.Payload>();
        var c = new HelperSelfHealCoordinator(
            sentinels.Add, _ => { }, NullLogger<HelperSelfHealCoordinator>.Instance);

        Assert.True(c.Consider(Strand(3), T0).Trigger);
        Assert.False(c.Consider(Strand(4), T0.AddMinutes(5)).Trigger);   // backoff
        Assert.True(c.Consider(Strand(5), T0.AddMinutes(11)).Trigger);   // spaced out
        Assert.Equal(2, sentinels.Count);
    }

    [Fact]
    public void Coordinator_ExhaustsAtCap_AndReportsExhausted()
    {
        var c = new HelperSelfHealCoordinator(
            _ => { }, _ => { }, NullLogger<HelperSelfHealCoordinator>.Instance);

        var t = T0;
        for (var i = 0; i < HelperSelfHealPolicy.MaxAttemptsPerWindow; i++)
        {
            Assert.True(c.Consider(Strand(3 + i), t).Trigger);
            t = t.AddMinutes(15);
        }

        Assert.False(c.Consider(Strand(99), t).Trigger);
        Assert.True(c.Snapshot().Exhausted);

        // The rolling window re-opens capacity once old attempts age out.
        Assert.True(c.Consider(Strand(99), T0.AddHours(25)).Trigger);
    }

    [Fact]
    public void Coordinator_SentinelWriteFailure_DoesNotConsumeAnAttempt()
    {
        var calls = 0;
        var c = new HelperSelfHealCoordinator(
            _ => { calls++; throw new IOException("disk full"); },
            _ => { },
            NullLogger<HelperSelfHealCoordinator>.Instance);

        var d = c.Consider(Strand(3), T0);

        Assert.False(d.Trigger);
        Assert.Equal("sentinel_write_failed", d.Reason);
        Assert.Equal(1, calls);
        Assert.Equal(0, c.Snapshot().AttemptsInWindow); // retries next cycle, not burned
    }
}
