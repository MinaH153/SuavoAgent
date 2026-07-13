using SuavoAgent.Helper.Companion;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Companion;

public sealed class CompanionStateLogicTests
{
    private static CompanionSignals Signals(
        bool coreConnected = true,
        bool observationActive = true,
        bool learningActive = false,
        PresenceMode presenceMode = PresenceMode.Idle,
        bool enabled = true,
        bool dryRun = false,
        bool paused = false,
        bool killed = false,
        bool compromised = false,
        bool synchronized = true) => new(
            coreConnected,
            observationActive,
            learningActive,
            presenceMode,
            enabled,
            dryRun,
            paused,
            killed,
            compromised,
            synchronized);

    [Fact]
    public void IdleHealthyRuntime_IsWatching()
        => Assert.Equal(CompanionState.Watching, CompanionStateLogic.Evaluate(Signals()).State);

    [Fact]
    public void AgentActivity_IsWorking()
        => Assert.Equal(
            CompanionState.Working,
            CompanionStateLogic.Evaluate(Signals(presenceMode: PresenceMode.Driving)).State);

    [Fact]
    public void PauseImmediatelyOverridesRecentAgentActivity()
        => Assert.Equal(
            CompanionState.Paused,
            CompanionStateLogic.Evaluate(Signals(
                presenceMode: PresenceMode.Driving,
                paused: true)).State);

    [Fact]
    public void HumanActivity_IsLearning_OnlyWhenBehavioralObserversAreActive()
    {
        var learning = CompanionStateLogic.Evaluate(Signals(
            learningActive: true,
            presenceMode: PresenceMode.Observing,
            paused: true));
        var notLearning = CompanionStateLogic.Evaluate(Signals(
            learningActive: false,
            presenceMode: PresenceMode.Observing,
            paused: true));

        Assert.Equal(CompanionState.Learning, learning.State);
        Assert.Equal(CompanionState.Paused, notLearning.State);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void MissingCoreOrObserverProof_IsOffline(bool coreConnected, bool observationActive)
        => Assert.Equal(
            CompanionState.Offline,
            CompanionStateLogic.Evaluate(Signals(
                coreConnected: coreConnected,
                observationActive: observationActive)).State);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ClosedOrPausedGate_IsPaused(bool enabled, bool paused)
        => Assert.Equal(
            CompanionState.Paused,
            CompanionStateLogic.Evaluate(Signals(enabled: enabled, paused: paused)).State);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void KillOrCompromise_IsNeedsAttention_EvenWhenOffline(bool killed, bool compromised)
        => Assert.Equal(
            CompanionState.NeedsAttention,
            CompanionStateLogic.Evaluate(Signals(
                coreConnected: false,
                killed: killed,
                compromised: compromised)).State);

    [Fact]
    public void ControlCapabilities_DoNotOfferResumeAcrossSafetyLatches()
    {
        var ordinaryPause = CompanionStateLogic.Evaluate(Signals(paused: true));
        var killed = CompanionStateLogic.Evaluate(Signals(paused: true, killed: true));
        var configDisabled = CompanionStateLogic.Evaluate(Signals(enabled: false));

        Assert.True(ordinaryPause.CanResume);
        Assert.False(killed.CanResume);
        Assert.False(configDisabled.CanResume);
        Assert.False(killed.CanPause);
    }

    [Fact]
    public void UnacknowledgedLocalControlIsNeedsAttention()
        => Assert.Equal(
            CompanionState.NeedsAttention,
            CompanionStateLogic.Evaluate(Signals(
                paused: true,
                synchronized: false)).State);
}
