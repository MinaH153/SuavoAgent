using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins the precedence-1 sandbox-explore carve-out (Codex-reviewed 2026-06-08). It Allows REAL execution of
/// click verbs in an allowlisted sandbox process — bypassing the autonomy-graduation gate that would
/// otherwise dry-run-and-terminate — while structurally DENYING type/press (Codex Q1 foreground-leak) and
/// any non-allowlisted process. Kill-switch / pause / unreadable gate state still hard-deny (fail-closed).
/// </summary>
public class SandboxExploreSafetyGateTests
{
    private static readonly AgentObjective Obj = new("learn calc", "task.explore", "ph");

    private static ActuationGateState CleanGate() =>
        new(Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null);

    private static SandboxExploreSafetyGate Gate(ActuationGateState? gs) =>
        new(() => gs, () => DateTimeOffset.UtcNow);

    private static NextAction Click(string process) =>
        NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = "Seven", ["process_name"] = process });

    [Fact]
    public void Allows_ClickByLabel_InAllowlistedProcess()
    {
        Assert.Equal(SafetyDecision.Allow, Gate(CleanGate()).GateAction(Click("calc.exe"), Obj).Decision);
    }

    [Fact]
    public void Denies_ClickByLabel_InNonAllowlistedProcess()
    {
        Assert.Equal(SafetyDecision.Deny, Gate(CleanGate()).GateAction(Click("notepad"), Obj).Decision);
        Assert.Equal(SafetyDecision.Deny, Gate(CleanGate()).GateAction(Click("chrome"), Obj).Decision);
        Assert.Equal(SafetyDecision.Deny, Gate(CleanGate()).GateAction(Click("PioneerRx"), Obj).Decision);
    }

    [Fact]
    public void Denies_TypeAndPress_EvenInAllowlistedProcess_V1()
    {
        var type = NextAction.Act("type_into_field", new Dictionary<string, object?> { ["text"] = "7", ["process_name"] = "calc.exe" });
        var press = NextAction.Act("press_keys", new Dictionary<string, object?> { ["chords"] = "Enter", ["process_name"] = "calc.exe" });
        Assert.Equal(SafetyDecision.Deny, Gate(CleanGate()).GateAction(type, Obj).Decision);
        Assert.Equal(SafetyDecision.Deny, Gate(CleanGate()).GateAction(press, Obj).Decision);
    }

    [Fact]
    public void Allows_LaunchSandboxApp_ForAllowlistedAppKey()
    {
        var launch = NextAction.Act("launch_sandbox_app", new Dictionary<string, object?> { ["app_key"] = "calculator" });
        Assert.Equal(SafetyDecision.Allow, Gate(CleanGate()).GateAction(launch, Obj).Decision);
    }

    [Fact]
    public void GateForcedDryRun_DowngradesAllowedActionToDry()
    {
        var gs = new ActuationGateState(Enabled: true, DryRun: true, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null);
        Assert.Equal(SafetyDecision.AllowDryRun, Gate(gs).GateAction(Click("calc.exe"), Obj).Decision);
    }

    [Fact]
    public void Preflight_FailsClosed_OnNullState_KillSwitch_AndPause()
    {
        Assert.Equal(SafetyDecision.Deny, Gate(null).Preflight(Obj).Decision);

        var killed = new ActuationGateState(Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null,
            KillSwitchTrippedUtc: DateTimeOffset.UtcNow);
        Assert.Equal(SafetyDecision.Deny, Gate(killed).Preflight(Obj).Decision);

        var paused = new ActuationGateState(Enabled: true, DryRun: false,
            PausedUntilUtc: DateTimeOffset.UtcNow.AddMinutes(5), PauseReason: "operator", KillSwitchTrippedUtc: null);
        Assert.Equal(SafetyDecision.Deny, Gate(paused).Preflight(Obj).Decision);
    }

    [Fact]
    public void Preflight_Allows_WhenGateClean()
    {
        Assert.Equal(SafetyDecision.Allow, Gate(CleanGate()).Preflight(Obj).Decision);
    }

    [Fact]
    public void AssertScrubbed_ReflectsScreenFlag()
    {
        var gate = Gate(CleanGate());
        Assert.True(gate.AssertScrubbed(new PerceivedScreen("h", Scrubbed: true, new[] { "button:X" })));
        Assert.False(gate.AssertScrubbed(new PerceivedScreen("h", Scrubbed: false, new[] { "button:X" })));
    }
}
