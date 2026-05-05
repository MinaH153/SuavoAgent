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
    public void SetDryRun_TogglesObservedFlag()
    {
        var gate = Build(enabled: true, dryRun: true);
        Assert.True(gate.IsDryRun);
        gate.SetDryRun(false);
        Assert.False(gate.IsDryRun);
    }
}
