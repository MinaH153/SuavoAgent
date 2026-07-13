using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

public sealed class CompositeSafetyGateScopeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly AgentStateDb _db = new(":memory:");

    public void Dispose() => _db.Dispose();

    [Fact]
    public void LegacyTaskEligibility_CannotAuthorizeDifferentAppActionOrExecutor()
    {
        const string pharmacyId = "ph-1";
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 1);
        ledger.RecordRun("pricing", pharmacyId, clean: true);

        var gate = new CompositeSafetyGate(
            gateState: () => new ActuationGateState(true, false, null, null, null),
            ledger,
            new NavigateSafetyOptions(
                EnableTaskAutonomy: true,
                ExecutorMode: PricingExecutorMode.UiaFirst,
                AllowLiveActuation: false),
            now: () => Now);
        var objective = new AgentObjective("click Save", "pricing", pharmacyId);
        var action = NextAction.Act("click_by_label", new Dictionary<string, object?>
        {
            ["process_name"] = "calc.exe",
            ["label"] = "Save",
        });

        var verdict = gate.GateAction(action, objective);

        Assert.Equal(SafetyDecision.AllowDryRun, verdict.Decision);
        Assert.Equal("supervised_autonomy_not_earned", verdict.Reason);
    }

    [Fact]
    public void ExactScopedEligibility_AuthorizesOnlyTheBoundExecutionClass()
    {
        const string pharmacyId = "22222222-2222-4222-8222-222222222222";
        using var signer = new EvidenceSigner();
        var options = new AgentOptions
        {
            AgentId = "11111111-1111-4111-8111-111111111111",
            PharmacyId = pharmacyId,
            MachineFingerprint = "workstation-01",
        };
        var ledger = new TaskAutonomyLedger(_db, 1, options, signer);
        var exactScope = TaskAutonomyScope.Create(
            "pricing", "pricing", "pioneerrx", "3.9.2",
            new string('b', 64), new string('c', 64), new string('d', 64),
            PricingExecutorMode.UiaFirst);
        ledger.RecordRun(new(
            Guid.NewGuid().ToString("D"),
            exactScope, true, 1, AutonomySemanticResult.Completed, true,
            new string('e', 64), Now));

        var gate = new CompositeSafetyGate(
            gateState: () => new ActuationGateState(true, false, null, null, null),
            ledger,
            new NavigateSafetyOptions(
                EnableTaskAutonomy: true,
                ExecutorMode: PricingExecutorMode.UiaFirst,
                AllowLiveActuation: false,
                AutonomyScopeFactory: (_, _, _) => exactScope),
            now: () => Now);
        var objective = new AgentObjective("click Save", "pricing", pharmacyId);
        var action = NextAction.Act("click_by_label", new Dictionary<string, object?>
        {
            ["process_name"] = "calc.exe",
            ["label"] = "Save",
        });

        var verdict = gate.GateAction(action, objective);

        Assert.Equal(SafetyDecision.Allow, verdict.Decision);
    }

    [Fact]
    public void OperatorApproval_CannotAuthorizeAppLessKeyboardAction()
    {
        var ledger = new TaskAutonomyLedger(_db, cleanRunsThreshold: 1);
        var gate = new CompositeSafetyGate(
            gateState: () => new ActuationGateState(true, false, null, null, null),
            ledger,
            new NavigateSafetyOptions(
                EnableTaskAutonomy: true,
                ExecutorMode: PricingExecutorMode.UiaFirst,
                AllowLiveActuation: false),
            now: () => Now);
        var objective = new AgentObjective("type", "pricing", "ph-1");
        var action = NextAction.Act("type_into_field", new Dictionary<string, object?>
        {
            ["text"] = "safe text",
        });

        var verdict = gate.GateAction(action, objective);

        Assert.Equal(SafetyDecision.Deny, verdict.Decision);
        Assert.Equal("target_process_unresolved", verdict.Reason);
    }

    private sealed class EvidenceSigner : IDeviceAuthoritySigner
    {
        public string KeyId => new string('a', 64);
        public SignedDeviceReceipt<AutonomyEvidenceDeviceReceipt> Sign(
            AutonomyEvidenceDeviceReceipt receipt) => new(
                receipt, KeyId, "device-signature", new string('9', 64));
        public SignedDeviceReceipt<PomActivationDeviceReceipt> Sign(
            PomActivationDeviceReceipt receipt) => throw new NotSupportedException();
        public SignedDeviceReceipt<RxSourceDeviceReceipt> Sign(
            RxSourceDeviceReceipt receipt) => throw new NotSupportedException();
        public SignedDeviceReceipt<SeedApplicationDeviceReceipt> Sign(
            SeedApplicationDeviceReceipt receipt) => throw new NotSupportedException();
        public SignedDeviceProvisioningProof SignProvisioningProof(
            DeviceProvisioningProofPayload proof) => throw new NotSupportedException();
        public SignedDeviceProbationHealth SignProbationHealth(
            DeviceProbationHealthFields health) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
