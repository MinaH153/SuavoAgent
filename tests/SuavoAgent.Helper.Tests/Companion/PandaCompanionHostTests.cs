using Serilog;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Companion;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Companion;

public sealed class PandaCompanionHostTests
{
    [Fact]
    public void ViewControls_PauseResumeAndIrreversiblyStopTheExistingGate()
    {
        var view = new FakeView();
        var gate = new ActuationGate(new ActuationConfig
        {
            Enabled = true,
            DryRun = false,
            UserInputPauseWindow = TimeSpan.FromMinutes(5),
        }, new LoggerConfiguration().CreateLogger());
        using var host = new PandaCompanionHost(
            view,
            gate,
            coreConnected: () => true,
            presenceMode: () => PresenceMode.Idle,
            coreControl: new FakeCoreControl(),
            new LoggerConfiguration().CreateLogger());
        host.SetObservationActive(true);

        Assert.Equal(CompanionState.Watching, view.Last!.State);

        view.RequestPause();
        Assert.Equal(CompanionState.Paused, view.Last!.State);
        Assert.Equal(DateTimeOffset.MaxValue, gate.Snapshot().PausedUntilUtc);

        view.RequestResume();
        Assert.Equal(CompanionState.Watching, view.Last!.State);
        Assert.Null(gate.CheckOrReject());

        view.RequestStop();
        Assert.Equal(CompanionState.NeedsAttention, view.Last!.State);
        Assert.NotNull(gate.Snapshot().KillSwitchTrippedUtc);

        view.RequestResume();
        Assert.Equal(CompanionState.NeedsAttention, view.Last!.State);
        Assert.NotNull(gate.Snapshot().KillSwitchTrippedUtc);
    }

    [Fact]
    public void FailedCoreResume_KeepsTheLocalMutationGatePaused()
    {
        var view = new FakeView();
        var gate = new ActuationGate(new ActuationConfig
        {
            Enabled = true,
            DryRun = false,
            UserInputPauseWindow = TimeSpan.FromMinutes(5),
        }, new LoggerConfiguration().CreateLogger());
        using var host = new PandaCompanionHost(
            view,
            gate,
            coreConnected: () => true,
            presenceMode: () => PresenceMode.Idle,
            coreControl: new FakeCoreControl(resumeApplied: false),
            new LoggerConfiguration().CreateLogger());

        view.RequestPause();
        view.RequestResume();

        Assert.Equal(DateTimeOffset.MaxValue, gate.Snapshot().PausedUntilUtc);
        Assert.NotNull(gate.CheckOrReject());
    }

    [Fact]
    public void FailedCorePauseIsExposedAsNeedsAttentionNotOrdinaryPause()
    {
        var view = new FakeView();
        var gate = NewGate();
        using var host = new PandaCompanionHost(
            view,
            gate,
            coreConnected: () => true,
            presenceMode: () => PresenceMode.Idle,
            coreControl: new FakeCoreControl(pauseApplied: false),
            new LoggerConfiguration().CreateLogger());
        host.SetObservationActive(true);

        view.RequestPause();

        Assert.Equal(CompanionState.NeedsAttention, view.Last!.State);
        Assert.Equal(DateTimeOffset.MaxValue, gate.Snapshot().PausedUntilUtc);
    }

    [Fact]
    public void WatchingExpiresUnlessObservationHealthIsRenewed()
    {
        var now = DateTimeOffset.Parse("2026-07-13T12:00:00Z");
        var view = new FakeView();
        using var host = new PandaCompanionHost(
            view,
            NewGate(),
            coreConnected: () => true,
            presenceMode: () => PresenceMode.Idle,
            coreControl: new FakeCoreControl(),
            new LoggerConfiguration().CreateLogger(),
            clock: () => now,
            observationLeaseDuration: TimeSpan.FromSeconds(45));

        host.SetObservationActive(true);
        Assert.Equal(CompanionState.Watching, view.Last!.State);

        now = now.AddSeconds(46);
        Assert.Equal(CompanionState.Offline, host.Refresh().State);

        host.SetObservationActive(true);
        Assert.Equal(CompanionState.Watching, view.Last!.State);
    }

    private static ActuationGate NewGate() => new(new ActuationConfig
    {
        Enabled = true,
        DryRun = false,
        UserInputPauseWindow = TimeSpan.FromMinutes(5),
    }, new LoggerConfiguration().CreateLogger());

    private sealed class FakeCoreControl(
        bool resumeApplied = true,
        bool pauseApplied = true) : IAutopilotControlClient
    {
        public Task<bool> PauseAsync(CancellationToken cancellationToken) => Task.FromResult(pauseApplied);
        public Task<bool> ResumeAsync(CancellationToken cancellationToken) => Task.FromResult(resumeApplied);
        public Task<bool> StopAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeView : IPandaCompanionView
    {
        public event Action? PauseRequested;
        public event Action? ResumeRequested;
        public event Action? StopRequested;
        public CompanionPresentation? Last { get; private set; }

        public void Start() { }
        public void Render(CompanionPresentation presentation) => Last = presentation;
        public void RequestPause() => PauseRequested?.Invoke();
        public void RequestResume() => ResumeRequested?.Invoke();
        public void RequestStop() => StopRequested?.Invoke();
        public void Dispose() { }
    }
}
