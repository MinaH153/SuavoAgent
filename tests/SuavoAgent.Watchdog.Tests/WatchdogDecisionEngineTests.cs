using SuavoAgent.Watchdog;
using Xunit;

namespace SuavoAgent.Watchdog.Tests;

public class WatchdogDecisionEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 4, 22, 12, 0, 0, TimeSpan.Zero);

    private WatchdogDecisionEngine Engine() => new()
    {
        UnhealthyGrace = TimeSpan.FromMinutes(5),
        RestartBackoff = TimeSpan.FromSeconds(60),
        EscalateAfterConsecutiveFailures = 3
    };

    [Fact]
    public void Running_IsDoNothing_AndClearsFailureCounter()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Core", T0) with { ConsecutiveRestartFailures = 2 };
        var (decision, next) = eng.Decide(ledger, ServiceState.Running, T0.AddMinutes(1));
        Assert.Equal(DecisionAction.DoNothing, decision.Action);
        Assert.Null(next.UnhealthySince);
        Assert.Equal(0, next.ConsecutiveRestartFailures);
    }

    [Fact]
    public void Unhealthy_WithinGrace_DoNothing()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Core", T0);
        var (decision, next) = eng.Decide(ledger, ServiceState.Stopped, T0.AddMinutes(2));
        Assert.Equal(DecisionAction.DoNothing, decision.Action);
        Assert.Equal(T0.AddMinutes(2), next.UnhealthySince);
    }

    [Fact]
    public void Unhealthy_AfterGrace_TriggersRestart()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Core", T0);
        var (_, afterFirst) = eng.Decide(ledger, ServiceState.Stopped, T0);
        var (decision, next) = eng.Decide(afterFirst, ServiceState.Stopped, T0.AddMinutes(6));
        Assert.Equal(DecisionAction.AttemptRestart, decision.Action);
        Assert.Equal(T0.AddMinutes(6), next.LastRestartAttemptAt);
    }

    [Fact]
    public void RestartBackoff_HonoredWithin60s()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Core", T0) with
        {
            UnhealthySince = T0,
            LastRestartAttemptAt = T0.AddMinutes(6),
            ConsecutiveRestartFailures = 1
        };
        var (decision, _) = eng.Decide(ledger, ServiceState.Stopped, T0.AddMinutes(6).AddSeconds(30));
        Assert.Equal(DecisionAction.DoNothing, decision.Action);
        Assert.Contains("backoff", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThreeConsecutiveFailures_EscalatesRepair()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Core", T0) with
        {
            UnhealthySince = T0,
            LastRestartAttemptAt = T0.AddMinutes(8),
            ConsecutiveRestartFailures = 3
        };
        var (decision, next) = eng.Decide(ledger, ServiceState.Stopped, T0.AddMinutes(10));
        Assert.Equal(DecisionAction.EscalateRepair, decision.Action);
        Assert.Equal(1, next.RepairInvocations);
        Assert.Equal(0, next.ConsecutiveRestartFailures);
    }

    [Fact]
    public void NotInstalled_EscalatesRepairImmediately()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Broker", T0);
        var (decision, next) = eng.Decide(ledger, ServiceState.NotInstalled, T0);
        Assert.Equal(DecisionAction.EscalateRepair, decision.Action);
        Assert.Equal(1, next.RepairInvocations);
    }

    [Fact]
    public void StartPending_ObservesWithoutAction()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Core", T0) with { UnhealthySince = T0 };
        var (decision, next) = eng.Decide(ledger, ServiceState.StartPending, T0.AddMinutes(6));
        Assert.Equal(DecisionAction.ObserveStartPending, decision.Action);
        Assert.NotNull(next.UnhealthySince); // still tracking unhealthy until we see RUNNING
    }

    [Fact]
    public void RecordRestartResult_Failure_IncrementsCounter()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Core", T0);
        var incremented = eng.RecordRestartResult(ledger, succeeded: false);
        Assert.Equal(1, incremented.ConsecutiveRestartFailures);
    }

    [Fact]
    public void RecordRestartResult_Success_ResetsCounter()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Core", T0) with { ConsecutiveRestartFailures = 2 };
        var reset = eng.RecordRestartResult(ledger, succeeded: true);
        Assert.Equal(0, reset.ConsecutiveRestartFailures);
    }

    [Fact]
    public void RestartSuccess_ThenHealthy_ResetsEverything()
    {
        var eng = Engine();
        var ledger = ServiceLedger.Initial("SuavoAgent.Core", T0);
        var (_, sawStopped) = eng.Decide(ledger, ServiceState.Stopped, T0.AddMinutes(6));
        var (_, afterAttempt) = eng.Decide(sawStopped, ServiceState.Stopped, T0.AddMinutes(7));
        var afterAttemptOk = eng.RecordRestartResult(afterAttempt, succeeded: true);
        var (_, backHealthy) = eng.Decide(afterAttemptOk, ServiceState.Running, T0.AddMinutes(8));
        Assert.Null(backHealthy.UnhealthySince);
        Assert.Equal(0, backHealthy.ConsecutiveRestartFailures);
    }
}
