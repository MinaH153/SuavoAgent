using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Security;
using Xunit;

namespace SuavoAgent.Helper.Tests.Security;

/// <summary>
/// The orchestrator is the on-box actuation half: it maps the corroboration verdict onto the REAL
/// ActuationGate primitives. Asserts the never-brick contract — Degrade is reversible (GateDisabled, not a
/// latch), Apoptosis latches the kill switch (with dry-run set first), and the recorded compromise level
/// only ever latches UP so a late degrade can't mask an apoptosis.
/// </summary>
public sealed class ApoptosisOrchestratorTests
{
    private static (ActuationGate gate, ApoptosisOrchestrator orch) Build()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        // Start with an OPEN gate (Enabled, not dry-run) so the transitions are observable.
        var gate = new ActuationGate(new ActuationConfig { Enabled = true, DryRun = false }, logger);
        return (gate, new ApoptosisOrchestrator(gate));
    }

    private static CorroborationResult R(CorroborationLevel level) => new(level, $"{level}.test");

    [Fact]
    public void Observe_LeavesGateOpen_NoCompromiseSignal()
    {
        var (gate, orch) = Build();
        orch.OnCompromise(R(CorroborationLevel.Observe));
        Assert.Null(gate.CheckOrReject());      // gate still open — nothing changed
        Assert.False(gate.IsDryRun);
        Assert.False(gate.Snapshot().CompromiseDetected);
    }

    [Fact]
    public void Degrade_IsReversible_DisabledNotLatched_SignalsDegrade()
    {
        var (gate, orch) = Build();
        orch.OnCompromise(R(CorroborationLevel.Degrade));

        Assert.True(gate.IsDryRun);
        var rej = gate.CheckOrReject();
        // GateDisabled (reversible by restart), NOT KillSwitchTripped — a misjudged backup tool recovers.
        Assert.Equal(ActuationRejectionCodes.GateDisabled, rej!.RejectionCode);

        var s = gate.Snapshot();
        Assert.True(s.CompromiseDetected);
        Assert.Equal("degrade", s.CompromiseLevel);
        Assert.NotNull(s.CompromiseAtUtc);
    }

    [Fact]
    public void Apoptosis_LatchesKillSwitch_DryRunFirst_SignalsApoptosis()
    {
        var (gate, orch) = Build();
        orch.OnCompromise(R(CorroborationLevel.Apoptosis));

        Assert.True(gate.IsDryRun);             // dry-run applied (the reversible window) before the latch
        Assert.Equal(ActuationRejectionCodes.KillSwitchTripped, gate.CheckOrReject()!.RejectionCode);
        // latch persists — every subsequent actuation is rejected
        Assert.Equal(ActuationRejectionCodes.KillSwitchTripped, gate.CheckOrReject()!.RejectionCode);

        var s = gate.Snapshot();
        Assert.True(s.CompromiseDetected);
        Assert.Equal("apoptosis", s.CompromiseLevel);
        Assert.Equal("Apoptosis.test", s.CompromiseReasonLabel);
    }

    [Fact]
    public void DegradeThenApoptosis_LevelLatchesUp_LateDegradeDoesNotMaskApoptosis()
    {
        var (gate, orch) = Build();
        orch.OnCompromise(R(CorroborationLevel.Degrade));
        orch.OnCompromise(R(CorroborationLevel.Apoptosis));
        Assert.Equal("apoptosis", gate.Snapshot().CompromiseLevel);
        Assert.Equal(ActuationRejectionCodes.KillSwitchTripped, gate.CheckOrReject()!.RejectionCode);

        // a late degrade must NOT downgrade the recorded apoptosis
        orch.OnCompromise(R(CorroborationLevel.Degrade));
        Assert.Equal("apoptosis", gate.Snapshot().CompromiseLevel);
    }
}
