using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Broker;
using Xunit;

namespace SuavoAgent.Broker.Tests;

// B5: when the privileged interactive-session launch (CreateProcessAsUser) keeps returning null —
// the Broker lacks SeTcbPrivilege (mis-registered as NetworkService) — the old code logged CRITICAL
// and returned, but CheckActiveSessions re-runs every 5s, so it retried FOREVER, spammed CRITICAL,
// and never triggered repair. These guard the launch-failure counter + the one-shot bootstrap-repair
// escalation that replaces the silent infinite retry. (A Helper crash, not a launch failure, never
// moves this counter — so "never launched" is distinguished from "crashed".)
public class SessionWatcherLaunchEscalationTests
{
    private sealed class FakeWatchdogProbe : IWatchdogServiceProbe
    {
        public int InvokeCount;
        public string? LastBootstrapPath;
        public bool ReturnValue = true;
        public bool IsWatchdogServiceInstalled() => true;
        public bool InvokeBootstrapRepair(string bootstrapPath, TimeSpan timeout)
        {
            InvokeCount++;
            LastBootstrapPath = bootstrapPath;
            return ReturnValue;
        }
    }

    private static SessionWatcher Create(IWatchdogServiceProbe probe, Func<string, bool>? fileExists = null) =>
        new(NullLogger<SessionWatcher>.Instance, probe, fileExists ?? (_ => true));

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, false)]
    [InlineData(3, false, true)]   // crosses the threshold
    [InlineData(4, false, true)]
    [InlineData(3, true, false)]   // already escalated → do not escalate again
    public void ShouldEscalateLaunchFailure_OnlyPastThresholdAndNotAlreadyEscalated(
        int failures, bool already, bool expected)
        => Assert.Equal(expected, SessionWatcher.ShouldEscalateLaunchFailure(failures, already));

    [Fact]
    public void RegisterLaunchFailure_EscalatesExactlyOnceAtThreshold()
    {
        var w = Create(new FakeWatchdogProbe());
        Assert.False(w.RegisterLaunchFailure()); // 1
        Assert.False(w.RegisterLaunchFailure()); // 2
        Assert.True(w.RegisterLaunchFailure());  // 3 → escalate
        Assert.Equal(3, w.ConsecutiveLaunchFailures);
        Assert.True(w.LaunchFailureEscalated);
        Assert.False(w.RegisterLaunchFailure()); // 4 → no double-escalation
        Assert.False(w.RegisterLaunchFailure()); // 5
        Assert.Equal(5, w.ConsecutiveLaunchFailures);
    }

    [Fact]
    public void RegisterLaunchSuccess_ResetsCounterAndEscalation_AndAllowsReEscalation()
    {
        var w = Create(new FakeWatchdogProbe());
        w.RegisterLaunchFailure();
        w.RegisterLaunchFailure();
        w.RegisterLaunchFailure(); // escalated
        Assert.True(w.LaunchFailureEscalated);

        w.RegisterLaunchSuccess();
        Assert.Equal(0, w.ConsecutiveLaunchFailures);
        Assert.False(w.LaunchFailureEscalated);

        // a fresh outage after recovery must be able to escalate again
        Assert.False(w.RegisterLaunchFailure());
        Assert.False(w.RegisterLaunchFailure());
        Assert.True(w.RegisterLaunchFailure());
    }

    [Fact]
    public void TryInvokeBootstrapRepair_InvokesProbe_WhenBootstrapPresent()
    {
        var probe = new FakeWatchdogProbe { ReturnValue = true };
        var w = Create(probe, fileExists: _ => true);

        Assert.True(w.TryInvokeBootstrapRepair());
        Assert.Equal(1, probe.InvokeCount);
        Assert.NotNull(probe.LastBootstrapPath);
        Assert.EndsWith("bootstrap.ps1", probe.LastBootstrapPath);
    }

    [Fact]
    public void TryInvokeBootstrapRepair_SkipsProbe_WhenBootstrapMissing()
    {
        var probe = new FakeWatchdogProbe();
        var w = Create(probe, fileExists: _ => false); // dev box / broken install

        Assert.False(w.TryInvokeBootstrapRepair());
        Assert.Equal(0, probe.InvokeCount); // never invoke repair without a bootstrap script
    }
}
