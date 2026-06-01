using SuavoAgent.Broker;
using Xunit;

namespace SuavoAgent.Broker.Tests;

// Regression guards for the Broker's Helper-launch fallback decision
// (SessionWatcher.MayUseProcessStartFallback).
//
// The bug (2026-06-01): when the privileged interactive-session launch
// (NativeProcess.LaunchInSession → WTSQueryUserToken + CreateProcessAsUser)
// returns null, the old code silently fell back to Process.Start, which on
// Windows launches the Helper in the BROKER's OWN session (Session 0) — an
// invisible desktop. A Helper there is BLIND: the intent cursor, screen
// capture, and UIA all run against a desktop no human can see, while the
// process looks alive and "green". That silent fallback masked a weeks-long
// outage (Broker mis-registered as NetworkService → no SeTcbPrivilege →
// WTSQueryUserToken 1314 → fallback → blind Helper).
//
// Fix: on Windows, REFUSE the blind Session-0 fallback and leave the Helper
// down so the cloud's helper-down watch flips the agent to degraded — the same
// fail-closed philosophy as the H-8 missing-manifest guard. On non-Windows
// there is no session isolation, so Process.Start remains the legitimate
// dev/test path.
public class SessionWatcherFallbackTests
{
    [Fact]
    public void Windows_PrivilegedLaunchFailure_RefusesBlindSession0Fallback()
    {
        // On Windows a null PID from the privileged launch must NOT degrade to a
        // Session-0 Helper. Fail closed instead.
        Assert.False(SessionWatcher.MayUseProcessStartFallback(isWindows: true));
    }

    [Fact]
    public void NonWindows_StillUsesProcessStartDevFallback()
    {
        // Linux/macOS dev boxes have no interactive-session isolation; the
        // Process.Start fallback is correct there.
        Assert.True(SessionWatcher.MayUseProcessStartFallback(isWindows: false));
    }
}
