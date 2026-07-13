using SuavoAgent.Core.Autonomy;
using Xunit;

namespace SuavoAgent.Core.Tests.Autonomy;

public sealed class AutopilotRunCoordinatorTests
{
    [Fact]
    public void Pause_CancelsEveryActiveKind_AndRejectsNewAdmission()
    {
        var coordinator = new AutopilotRunCoordinator();
        using var navigation = coordinator.Register(
            AutopilotRunKind.Navigation,
            CancellationToken.None);
        using var workflow = coordinator.Register(
            AutopilotRunKind.Workflow,
            CancellationToken.None);

        var receipt = coordinator.ApplyControl(
            AutopilotControlAction.Pause,
            "companion_pause");

        Assert.True(receipt.Applied);
        Assert.Equal(2, receipt.SignalledRunCount);
        Assert.True(navigation.Token.IsCancellationRequested);
        Assert.True(workflow.Token.IsCancellationRequested);
        using var rejected = coordinator.Register(
            AutopilotRunKind.Pricing,
            CancellationToken.None);
        Assert.False(rejected.Admitted);
        Assert.Equal("autopilot_paused", rejected.RejectionCode);
        Assert.True(rejected.Token.IsCancellationRequested);
    }

    [Fact]
    public void Resume_AfterPause_AllowsNewRun_ButNeverClearsStopLatch()
    {
        var coordinator = new AutopilotRunCoordinator();
        coordinator.ApplyControl(AutopilotControlAction.Pause, "operator_pause");
        var resume = coordinator.ApplyControl(
            AutopilotControlAction.Resume,
            "operator_resume");
        using var admitted = coordinator.Register(
            AutopilotRunKind.Workflow,
            CancellationToken.None);

        Assert.True(resume.Applied);
        Assert.True(admitted.Admitted);

        coordinator.ApplyControl(AutopilotControlAction.Stop, "operator_stop");
        var refusedResume = coordinator.ApplyControl(
            AutopilotControlAction.Resume,
            "operator_resume");
        using var stopped = coordinator.Register(
            AutopilotRunKind.Navigation,
            CancellationToken.None);

        Assert.False(refusedResume.Applied);
        Assert.Equal("stop_latched", refusedResume.Code);
        Assert.False(stopped.Admitted);
        Assert.Equal("autopilot_stopped", stopped.RejectionCode);
    }

    [Fact]
    public void HostileCancellationCallback_DoesNotBlockOtherRuns()
    {
        var coordinator = new AutopilotRunCoordinator();
        using var first = coordinator.Register(
            AutopilotRunKind.Pricing,
            CancellationToken.None);
        using var second = coordinator.Register(
            AutopilotRunKind.Navigation,
            CancellationToken.None);
        using var registration = first.Token.Register(
            () => throw new InvalidOperationException("callback failure"));

        var receipt = coordinator.ApplyControl(
            AutopilotControlAction.Stop,
            "operator_stop");

        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.Equal(1, receipt.CancellationSignalFailureCount);
    }

    [Fact]
    public void ReleasedLease_IsNotCountedOrSignalledLater()
    {
        var coordinator = new AutopilotRunCoordinator();
        var completed = coordinator.Register(
            AutopilotRunKind.DeliveryWriteback,
            CancellationToken.None);
        completed.Dispose();

        var receipt = coordinator.ApplyControl(
            AutopilotControlAction.Pause,
            "operator_pause");

        Assert.Equal(0, receipt.SignalledRunCount);
        Assert.Equal(0, coordinator.Snapshot().ActiveRunCount);
    }

    [Fact]
    public void SelectiveCancellation_StopsOnlyPricingWithoutChangingGlobalControl()
    {
        var coordinator = new AutopilotRunCoordinator();
        using var pricing = coordinator.Register(
            AutopilotRunKind.Pricing,
            CancellationToken.None);
        using var navigation = coordinator.Register(
            AutopilotRunKind.Navigation,
            CancellationToken.None);

        var receipt = coordinator.CancelRuns(AutopilotRunKind.Pricing);

        Assert.Equal(AutopilotRunKind.Pricing, receipt.Kind);
        Assert.Equal(1, receipt.SignalledRunCount);
        Assert.Equal(0, receipt.CancellationSignalFailureCount);
        Assert.True(pricing.Token.IsCancellationRequested);
        Assert.False(navigation.Token.IsCancellationRequested);
        Assert.False(coordinator.Snapshot().Paused);
        Assert.False(coordinator.Snapshot().Stopped);
    }

    [Theory]
    [InlineData(@"Jane Doe C:\Patients\rx.txt")]
    [InlineData("")]
    [InlineData("UPPERCASE")]
    public void FreeTextReason_IsReducedToFixedSafeCode(string input)
    {
        var coordinator = new AutopilotRunCoordinator();

        var receipt = coordinator.ApplyControl(
            AutopilotControlAction.Pause,
            input);

        Assert.Equal("local_operator_control", receipt.ReasonCode);
    }

    [Fact]
    public void LocalResume_RequiresTheExactCurrentPauseGeneration()
    {
        var coordinator = new AutopilotRunCoordinator();
        var pause = coordinator.ApplyLocalControl(
            AutopilotControlAction.Pause,
            "companion_control",
            expectedGeneration: null);

        var replayedOldResume = coordinator.ApplyLocalControl(
            AutopilotControlAction.Resume,
            "companion_control",
            expectedGeneration: pause.ControlGeneration - 1);
        Assert.False(replayedOldResume.Applied);
        Assert.Equal("control_generation_mismatch", replayedOldResume.Code);
        Assert.True(coordinator.Snapshot().Paused);

        var currentResume = coordinator.ApplyLocalControl(
            AutopilotControlAction.Resume,
            "companion_control",
            expectedGeneration: pause.ControlGeneration);
        Assert.True(currentResume.Applied);
        Assert.False(coordinator.Snapshot().Paused);
    }

    [Fact]
    public void OldResumeCannotReopenANewerPause()
    {
        var coordinator = new AutopilotRunCoordinator();
        var firstPause = coordinator.ApplyLocalControl(
            AutopilotControlAction.Pause,
            "companion_control",
            null);
        var secondPause = coordinator.ApplyLocalControl(
            AutopilotControlAction.Pause,
            "companion_control",
            null);

        var stale = coordinator.ApplyLocalControl(
            AutopilotControlAction.Resume,
            "companion_control",
            firstPause.ControlGeneration);

        Assert.False(stale.Applied);
        Assert.Equal(secondPause.ControlGeneration, stale.ControlGeneration);
        Assert.True(coordinator.Snapshot().Paused);
    }
}
