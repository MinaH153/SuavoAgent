using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// Bug 21 (MinaH153/SuavoAgent#63) — workflow-level dry_run must propagate
/// into the Helper's actuation surface and OR with the local
/// <see cref="ActuationGate"/> dry-run. Either flag forces dry-run; real
/// input only fires when BOTH the cloud workflow and the local gate agree
/// it should. Fail-closed.
///
/// These tests exercise the three dry-run combinations (which never touch
/// Win32, so they run cross-platform on the same Linux/Mac CI as everything
/// else). The (req=F, gate=F) "actually fires SendInput" case lives in
/// Queen-side manual verification + the existing Windows-only e2e battery.
/// </summary>
public sealed class SendInputDriverDryRunTests
{
    private static SendInputDriver BuildDriver(bool gateDryRun)
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var config = new ActuationConfig
        {
            Enabled = true,
            DryRun = gateDryRun,
            UserInputPauseWindow = TimeSpan.FromMinutes(5),
        };
        var gate = new ActuationGate(config, logger);
        return new SendInputDriver(gate, config, logger);
    }

    // ── TypeText ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TypeText_RequestTrue_GateFalse_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: false);
        var req = new TypeTextRequest(Text: "hello", ClearFirst: false, PerKeyDelayMs: 0, DryRun: true);
        var result = await driver.TypeTextAsync(req, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task TypeText_RequestFalse_GateTrue_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: true);
        var req = new TypeTextRequest(Text: "hello", ClearFirst: false, PerKeyDelayMs: 0, DryRun: false);
        var result = await driver.TypeTextAsync(req, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task TypeText_RequestTrue_GateTrue_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: true);
        var req = new TypeTextRequest(Text: "hello", ClearFirst: false, PerKeyDelayMs: 0, DryRun: true);
        var result = await driver.TypeTextAsync(req, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    // ── PressKeys ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PressKeys_RequestTrue_GateFalse_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: false);
        var req = new PressKeysRequest(Chords: new[] { "Enter" }, InterChordDelayMs: 0, DryRun: true);
        var result = await driver.PressKeysAsync(req, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task PressKeys_RequestFalse_GateTrue_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: true);
        var req = new PressKeysRequest(Chords: new[] { "Enter" }, InterChordDelayMs: 0, DryRun: false);
        var result = await driver.PressKeysAsync(req, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task PressKeys_RequestTrue_GateTrue_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: true);
        var req = new PressKeysRequest(Chords: new[] { "Enter" }, InterChordDelayMs: 0, DryRun: true);
        var result = await driver.PressKeysAsync(req, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    // ── LaunchSandboxApp ────────────────────────────────────────────────────

    [Fact]
    public async Task LaunchSandboxApp_RequestTrue_GateFalse_EffectiveTrue_NoProcessStarted()
    {
        var driver = BuildDriver(gateDryRun: false);
        var req = new LaunchSandboxAppRequest(AppKey: "calculator", DryRun: true);
        var result = await driver.LaunchSandboxAppAsync(req, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        // Bug 21 acceptance: when the cloud requested dry-run, Process.Start
        // must NOT fire even if the local gate allows live actuation.
    }

    [Fact]
    public async Task LaunchSandboxApp_RequestFalse_GateTrue_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: true);
        var req = new LaunchSandboxAppRequest(AppKey: "calculator", DryRun: false);
        var result = await driver.LaunchSandboxAppAsync(req, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task LaunchSandboxApp_RequestTrue_GateTrue_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: true);
        var req = new LaunchSandboxAppRequest(AppKey: "calculator", DryRun: true);
        var result = await driver.LaunchSandboxAppAsync(req, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    // ── ClickAt (label-resolved → primitive click path) ─────────────────────

    [Fact]
    public async Task ClickAt_RequestTrue_GateFalse_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: false);
        var result = await driver.ClickAtAsync(x: 100, y: 100, dryRun: true, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task ClickAt_RequestFalse_GateTrue_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: true);
        var result = await driver.ClickAtAsync(x: 100, y: 100, dryRun: false, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task ClickAt_RequestTrue_GateTrue_EffectiveTrue()
    {
        var driver = BuildDriver(gateDryRun: true);
        var result = await driver.ClickAtAsync(x: 100, y: 100, dryRun: true, CancellationToken.None);
        Assert.True(result.Ok);
        Assert.True(result.DryRun);
    }

    // ── Pre-gate rejection paths must also report effective dry-run ─────────

    [Fact]
    public async Task TypeText_PhiPatternRejection_ReportsEffectiveDryRun()
    {
        var driver = BuildDriver(gateDryRun: false);
        // SSN pattern triggers PhiPatternGuard rejection BEFORE the gate check,
        // so the rejection's DryRun must come from the request when the gate
        // is live — otherwise a rejected request could lie about whether the
        // attempt would have hit real input.
        var req = new TypeTextRequest(Text: "123-45-6789", ClearFirst: false, PerKeyDelayMs: 0, DryRun: true);
        var result = await driver.TypeTextAsync(req, CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.PhiPatternDetected, result.RejectionCode);
        Assert.True(result.DryRun);
    }

    [Fact]
    public async Task LaunchSandboxApp_Notepad_IsAlwaysRejected()
    {
        var driver = BuildDriver(gateDryRun: true);
        var result = await driver.LaunchSandboxAppAsync(
            new LaunchSandboxAppRequest("notepad", DryRun: true),
            CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal(ActuationRejectionCodes.AppNotInAllowlist, result.RejectionCode);
    }
}
