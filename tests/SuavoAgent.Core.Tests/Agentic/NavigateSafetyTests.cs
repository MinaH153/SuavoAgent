using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Precedence-1 safety decisions for the navigate loop. Every branch fails closed; the
/// never-blind-on-live-PMS and supervised-autonomy paths are asserted explicitly.
/// </summary>
public sealed class NavigateSafetyTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 4, 12, 0, 0, TimeSpan.Zero);

    private static ActuationGateState Gate(
        bool enabled = true, bool dryRun = false, DateTimeOffset? pausedUntil = null, DateTimeOffset? killTripped = null)
        => new(enabled, dryRun, pausedUntil, pausedUntil is null ? null : "user", killTripped);

    // --- Preflight -----------------------------------------------------------

    [Fact]
    public void Preflight_NullGateState_DeniesFailClosed()
    {
        var v = NavigateSafety.EvaluatePreflight(null, Now);
        Assert.Equal(SafetyDecision.Deny, v.Decision);
        Assert.Equal("gate_state_unavailable", v.Reason);
    }

    [Fact]
    public void Preflight_KillSwitchTripped_Denies()
    {
        var v = NavigateSafety.EvaluatePreflight(Gate(killTripped: Now), Now);
        Assert.Equal(SafetyDecision.Deny, v.Decision);
        Assert.Equal("kill_switch", v.Reason);
    }

    [Fact]
    public void Preflight_GateDisabled_Denies()
    {
        var v = NavigateSafety.EvaluatePreflight(Gate(enabled: false), Now);
        Assert.Equal(SafetyDecision.Deny, v.Decision);
    }

    [Fact]
    public void Preflight_PausedInFuture_Denies_UserActive()
    {
        var v = NavigateSafety.EvaluatePreflight(Gate(pausedUntil: Now.AddMinutes(2)), Now);
        Assert.Equal(SafetyDecision.Deny, v.Decision);
        Assert.Equal("paused_user_active", v.Reason);
    }

    [Fact]
    public void Preflight_PauseExpired_Allows()
    {
        var v = NavigateSafety.EvaluatePreflight(Gate(pausedUntil: Now.AddMinutes(-1)), Now);
        Assert.Equal(SafetyDecision.Allow, v.Decision);
    }

    [Fact]
    public void Preflight_LivePms_UiaFirst_NotAllowed_DeniesNeverBlind()
    {
        var v = NavigateSafety.EvaluateTarget(
            Gate(), Now, @"C:\Program Files\PioneerRx\PIONEERPHARMACY.EXE",
            PricingExecutorMode.UiaFirst, allowLiveActuation: false);
        Assert.Equal(SafetyDecision.Deny, v.Decision);
        Assert.Equal("never_blind_live_pms", v.Reason);
    }

    [Fact]
    public void Target_LivePms_UiAction_DeniesEvenWhenPricingExecutorIsSqlFirst()
    {
        var v = NavigateSafety.EvaluateTarget(
            Gate(), Now, "PioneerPharmacy.exe", PricingExecutorMode.SqlFirst, allowLiveActuation: false);
        Assert.Equal(SafetyDecision.Deny, v.Decision);
        Assert.Equal("never_blind_live_pms", v.Reason);
    }

    [Fact]
    public void Preflight_LivePms_UiaFirst_ExplicitlyAllowed_Allows()
    {
        var v = NavigateSafety.EvaluateTarget(
            Gate(), Now, "PioneerPharmacy.exe", PricingExecutorMode.UiaFirst, allowLiveActuation: true);
        Assert.Equal(SafetyDecision.Allow, v.Decision);
    }

    [Fact]
    public void TargetProcess_DerivesLivePmsFromAction_NotCallerPosture()
    {
        var action = NextAction.Act("click_by_signature", new Dictionary<string, object?>
        {
            ["process_name"] = "PioneerPharmacyHost.exe",
        });

        Assert.Equal("PioneerPharmacyHost.exe", NavigateSafety.TargetProcess(action));
        var verdict = NavigateSafety.EvaluateTarget(
            Gate(), Now, NavigateSafety.TargetProcess(action), PricingExecutorMode.VisionFirst, false);
        Assert.Equal(SafetyDecision.Deny, verdict.Decision);
    }

    // --- GateAction ----------------------------------------------------------

    [Fact]
    public void GateAction_NonDestructive_AllowsRegardlessOfAutonomy()
    {
        var wait = NextAction.Act("wait_for_element", new Dictionary<string, object?> { ["label"] = "X" });
        var v = NavigateSafety.EvaluateGateAction(wait, mayRunUnsupervised: false, gateForcesDryRun: false);
        Assert.Equal(SafetyDecision.Allow, v.Decision);
    }

    [Fact]
    public void GateAction_Destructive_AutonomyEarned_Allows()
    {
        var click = NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = "Save" });
        var v = NavigateSafety.EvaluateGateAction(click, mayRunUnsupervised: true, gateForcesDryRun: false);
        Assert.Equal(SafetyDecision.Allow, v.Decision);
    }

    [Fact]
    public void GateAction_Destructive_AutonomyEarned_ButGateForcesDryRun_Dry()
    {
        var click = NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = "Save" });
        var v = NavigateSafety.EvaluateGateAction(click, mayRunUnsupervised: true, gateForcesDryRun: true);
        Assert.Equal(SafetyDecision.AllowDryRun, v.Decision);
    }

    [Fact]
    public void GateAction_Destructive_AutonomyNotEarned_SupervisedDryRun()
    {
        var type = NextAction.Act("type_into_field", new Dictionary<string, object?> { ["text"] = "hi" });
        var v = NavigateSafety.EvaluateGateAction(type, mayRunUnsupervised: false, gateForcesDryRun: false);
        Assert.Equal(SafetyDecision.AllowDryRun, v.Decision);
        Assert.Equal("supervised_autonomy_not_earned", v.Reason);
    }

    [Theory]
    [InlineData("click_by_label", true)]
    [InlineData("click_by_signature", true)] // template replay — MUST be gated by autonomy (Codex P1 regression guard)
    [InlineData("type_into_field", true)]
    [InlineData("press_keys", true)]
    [InlineData("launch_sandbox_app", true)]
    [InlineData("wait_for_element", false)]
    public void IsDestructive_ClassifiesVerbs(string verb, bool expected)
    {
        Assert.Equal(expected, NavigateSafety.IsDestructive(NextAction.Act(verb)));
    }

    [Fact]
    public void IsDestructive_NonActKinds_AreNotDestructive()
    {
        Assert.False(NavigateSafety.IsDestructive(NextAction.Done));
        Assert.False(NavigateSafety.IsDestructive(NextAction.Escalate("x")));
    }
}
