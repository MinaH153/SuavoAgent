using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Security;
using Xunit;

namespace SuavoAgent.Helper.Tests.Security;

/// <summary>
/// The orchestrator is the on-box actuation half: it maps the corroboration verdict onto the REAL
/// ActuationGate primitives. Asserts the never-brick contract — Degrade blocks through the recorded
/// compromise without latching the kill switch, Apoptosis latches the kill switch (with dry-run set first),
/// and the recorded compromise level
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

    private static CorroborationResult R(CorroborationLevel level) => new(
        level,
        level == CorroborationLevel.Apoptosis
            ? HoneytokenReasonLabels.SensitiveShell
            : HoneytokenReasonLabels.UnexpectedProcess);

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
    public void Degrade_RecordsCompromiseWithoutLatchingKillSwitch()
    {
        var (gate, orch) = Build();
        orch.OnCompromise(R(CorroborationLevel.Degrade));

        Assert.True(gate.IsDryRun);
        var rej = gate.CheckOrReject();
        // The compromise receipt is now the highest-priority rejection. This is
        // still NOT KillSwitchTripped: a Helper restart can recover a false alarm.
        Assert.Equal(ActuationRejectionCodes.CompromiseDetected, rej!.RejectionCode);

        var s = gate.Snapshot();
        Assert.False(s.Enabled);
        Assert.Null(s.KillSwitchTrippedUtc);
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
        Assert.Equal(HoneytokenReasonLabels.SensitiveShell, s.CompromiseReasonLabel);
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

    [Fact]
    public void DynamicReasonLabel_IsNormalizedBeforeGateAuditOrLogState()
    {
        var (gate, orch) = Build();

        orch.OnCompromise(new CorroborationResult(
            CorroborationLevel.Degrade,
            "Jane_Doe_01-15-1990"));

        Assert.Equal(
            HoneytokenReasonLabels.UnknownProcess,
            gate.Snapshot().CompromiseReasonLabel);
    }
}
