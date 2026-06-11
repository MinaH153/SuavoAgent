using System;
using SuavoAgent.Broker;
using Xunit;

namespace SuavoAgent.Broker.Tests;

// Session-transition strand guard + restart-sentinel anti-thrash.
//
// The bug (live box, 2026-06-11): after a console-session change (CRD/RDP attach, fast user
// switch) the SessionWatcher launched a NEW Helper for the new session but left the OLD-session
// Helper alive. That blind survivor still owned the SINGLE-instance Core→Helper command pipe,
// so the fresh Helper could never bind it — commands stranded ("Helper did not answer ping")
// while heartbeats stayed green. CleanupDeadHelpers only reaps DEAD processes and
// ReconcileOrphanHelpers only runs at Broker STARTUP, so nothing ever killed the survivor.
//
// Fix: every watch tick, any Helper running OUTSIDE the active console session is stale —
// blind by definition (the pricing pre-flight refuses it anyway) — and gets killed so the pipe
// frees up for the Helper that can actually see the screen. These tests pin the pure decision.
public class SessionWatcherStaleSessionTests
{
    [Fact]
    public void HelperInActiveSession_IsNotStale()
    {
        var stale = SessionWatcher.StaleSessionHelperPids(
            new[] { (Pid: 100, SessionId: 2u) }, activeSessionId: 2u);

        Assert.Empty(stale);
    }

    [Fact]
    public void HelperLeftInOldSession_AfterTransition_IsStale()
    {
        // The exact live-box scenario: session switched 2 → 3; old Helper (session 2) must die.
        var stale = SessionWatcher.StaleSessionHelperPids(
            new[] { (Pid: 100, SessionId: 2u), (Pid: 200, SessionId: 3u) }, activeSessionId: 3u);

        Assert.Equal(new[] { 100 }, stale);
    }

    [Fact]
    public void Session0Helper_IsAlwaysStale()
    {
        // A Session-0 Helper is the blind-actuation failure — never legitimate when a real
        // console session is active.
        var stale = SessionWatcher.StaleSessionHelperPids(
            new[] { (Pid: 100, SessionId: 0u) }, activeSessionId: 1u);

        Assert.Equal(new[] { 100 }, stale);
    }

    [Fact]
    public void MultipleStaleSessions_AllKilled()
    {
        var stale = SessionWatcher.StaleSessionHelperPids(
            new[] { (Pid: 1, SessionId: 1u), (Pid: 2, SessionId: 2u), (Pid: 3, SessionId: 4u) },
            activeSessionId: 4u);

        Assert.Equal(new HashSet<int> { 1, 2 }, stale);
    }

    [Fact]
    public void NoHelpers_NothingToKill()
    {
        var stale = SessionWatcher.StaleSessionHelperPids(
            Array.Empty<(int, uint)>(), activeSessionId: 1u);

        Assert.Empty(stale);
    }
}

// restart_helper sentinel honor rule: even if Core spams requests (bug, repeated operator
// clicks, self-heal gone wrong), the Broker never cycles the Helper more than once per
// MinHelperRestartSpacing — the Broker-side half of the no-thrash guarantee.
public class SessionWatcherRestartRequestTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstRequestEver_IsHonored()
    {
        Assert.True(SessionWatcher.ShouldHonorRestartRequest(
            payloadValid: true, lastRestartAt: DateTimeOffset.MinValue, now: T0));
    }

    [Fact]
    public void RequestInsideSpacingWindow_IsIgnored()
    {
        Assert.False(SessionWatcher.ShouldHonorRestartRequest(
            payloadValid: true, lastRestartAt: T0.AddSeconds(-30), now: T0));
    }

    [Fact]
    public void RequestPastSpacingWindow_IsHonored()
    {
        Assert.True(SessionWatcher.ShouldHonorRestartRequest(
            payloadValid: true, lastRestartAt: T0 - SessionWatcher.MinHelperRestartSpacing, now: T0));
    }

    [Fact]
    public void InvalidPayload_IsNeverHonored()
    {
        Assert.False(SessionWatcher.ShouldHonorRestartRequest(
            payloadValid: false, lastRestartAt: DateTimeOffset.MinValue, now: T0));
    }
}
