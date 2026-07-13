using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class ActuationGateTests
{
    private static ActuationGate Build(bool enabled = true, bool dryRun = true) =>
        new(new ActuationConfig
        {
            Enabled = enabled,
            DryRun = dryRun,
            UserInputPauseWindow = TimeSpan.FromMinutes(5),
        }, new LoggerConfiguration().CreateLogger());

    [Fact]
    public void DefaultGate_OpenWhenEnabled()
    {
        var gate = Build(enabled: true);
        Assert.Null(gate.CheckOrReject());
    }

    [Fact]
    public void DisabledGate_RejectsWithGateDisabled()
    {
        var gate = Build(enabled: false);
        var rejection = gate.CheckOrReject();
        Assert.NotNull(rejection);
        Assert.False(rejection!.Ok);
        Assert.Equal(ActuationRejectionCodes.GateDisabled, rejection.RejectionCode);
    }

    [Fact]
    public void KillSwitch_TripsAndStaysTripped()
    {
        var gate = Build(enabled: true);
        gate.TripKillSwitch("test");
        var first = gate.CheckOrReject();
        Assert.NotNull(first);
        Assert.Equal(ActuationRejectionCodes.KillSwitchTripped, first!.RejectionCode);

        // SetEnabled(true) does not un-trip the kill switch — kill is sticky.
        gate.SetEnabled(true, "operator-attempt");
        var second = gate.CheckOrReject();
        Assert.NotNull(second);
        Assert.Equal(ActuationRejectionCodes.KillSwitchTripped, second!.RejectionCode);
    }

    [Fact]
    public void UserInputDetected_PausesUntilPlusWindow()
    {
        var gate = Build(enabled: true);
        gate.NotifyUserInputDetected("keyboard");
        var snapshot = gate.Snapshot();
        Assert.NotNull(snapshot.PausedUntilUtc);
        Assert.True(snapshot.PausedUntilUtc!.Value > DateTimeOffset.UtcNow);
        var rejection = gate.CheckOrReject();
        Assert.NotNull(rejection);
        Assert.Equal(ActuationRejectionCodes.GatePaused, rejection!.RejectionCode);
    }

    [Fact]
    public void NotifyUserInputDetected_OnlyExtendsPause_NeverShrinks()
    {
        var gate = Build(enabled: true);
        gate.NotifyUserInputDetected("keyboard");
        var firstUntil = gate.Snapshot().PausedUntilUtc;
        Thread.Sleep(5);
        gate.NotifyUserInputDetected("mouse");
        var secondUntil = gate.Snapshot().PausedUntilUtc;
        Assert.True(secondUntil >= firstUntil);
    }

    [Fact]
    public void ClearPause_AllowsActuation_IfStillEnabled()
    {
        var gate = Build(enabled: true);
        gate.NotifyUserInputDetected("keyboard");
        gate.ClearPause();
        Assert.Null(gate.CheckOrReject());
    }

    [Fact]
    public void PauseUntilResumed_IsIndefiniteAndReversible()
    {
        var gate = Build(enabled: true);
        gate.PauseUntilResumed();

        Assert.Equal(DateTimeOffset.MaxValue, gate.Snapshot().PausedUntilUtc);
        Assert.Equal(ActuationRejectionCodes.GatePaused, gate.CheckOrReject()!.RejectionCode);

        gate.ClearPause();
        Assert.Null(gate.CheckOrReject());
    }

    [Fact]
    public void SetDryRun_TogglesObservedFlag()
    {
        var gate = Build(enabled: true, dryRun: true);
        Assert.True(gate.IsDryRun);
        gate.SetDryRun(false);
        Assert.False(gate.IsDryRun);
    }

    [Fact]
    public void CheckLiveOrReject_RejectsDryRunAtomically()
    {
        var gate = Build(enabled: true, dryRun: true);

        var rejection = gate.CheckLiveOrReject();

        Assert.Equal(ActuationRejectionCodes.GateDryRun, rejection!.RejectionCode);
        Assert.True(rejection.DryRun);
    }

    [Fact]
    public void CheckLiveOrReject_AllowsOnlyFullyOpenGate()
    {
        var gate = Build(enabled: true, dryRun: false);

        Assert.Null(gate.CheckLiveOrReject());

        gate.NotifyUserInputDetected("keyboard");
        Assert.Equal(ActuationRejectionCodes.GatePaused, gate.CheckLiveOrReject()!.RejectionCode);
    }

    [Theory]
    [InlineData("disabled", ActuationRejectionCodes.GateDisabled)]
    [InlineData("paused", ActuationRejectionCodes.GatePaused)]
    [InlineData("dry_run", ActuationRejectionCodes.GateDryRun)]
    [InlineData("compromised", ActuationRejectionCodes.CompromiseDetected)]
    [InlineData("killed", ActuationRejectionCodes.KillSwitchTripped)]
    public void ExecuteLiveMutationOrReject_InvokesZeroMutations_WhenAnyAxisIsClosed(
        string axis,
        string expectedCode)
    {
        var gate = Build(enabled: true, dryRun: false);
        switch (axis)
        {
            case "disabled": gate.SetEnabled(false, "test"); break;
            case "paused": gate.PauseUntilResumed(); break;
            case "dry_run": gate.SetDryRun(true); break;
            case "compromised": gate.RecordHoneytokenCompromise("degrade", "test"); break;
            case "killed": gate.TripKillSwitch("test"); break;
        }
        var mutations = 0;

        var rejection = gate.ExecuteLiveMutationOrReject(() => mutations++);

        Assert.Equal(expectedCode, rejection!.RejectionCode);
        Assert.Equal(0, mutations);
    }

    [Fact]
    public async Task ExecuteLiveMutationOrReject_OrdersPauseAfterInFlightPrimitive_ThenStopsNextPrimitive()
    {
        var gate = Build(enabled: true, dryRun: false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var mutations = 0;
        var first = Task.Run(() => gate.ExecuteLiveMutationOrReject(() =>
        {
            Interlocked.Increment(ref mutations);
            entered.Set();
            release.Wait();
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));
        var pause = Task.Run(gate.PauseUntilResumed);
        Assert.False(pause.IsCompleted);
        release.Set();
        Assert.Null(await first);
        await pause;

        var second = gate.ExecuteLiveMutationOrReject(() => Interlocked.Increment(ref mutations));

        Assert.Equal(ActuationRejectionCodes.GatePaused, second!.RejectionCode);
        Assert.Equal(1, mutations);
    }
}
