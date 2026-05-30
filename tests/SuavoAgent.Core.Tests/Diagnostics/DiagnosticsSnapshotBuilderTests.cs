using System.Text.Json;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Diagnostics;
using Xunit;

namespace SuavoAgent.Core.Tests.Diagnostics;

/// <summary>
/// Pins the <c>fetch_diagnostics</c> snapshot: it must carry enough to debug a
/// box remotely (config + SQL wiring + helper/IPC + error-mesh) while NEVER
/// shipping a secret, and must survive <see cref="OutboundPhiGuard"/> — the same
/// guard the real ack POST runs through. See the agent-as-bridge decision.
/// </summary>
public class DiagnosticsSnapshotBuilderTests
{
    private const string SecretMarker = "SUPER_SECRET";

    private static AgentOptions OptionsWithSecrets() => new()
    {
        CloudUrl = "https://suavollc.com",
        ApiKey = "ak_" + SecretMarker + "_KEY",
        AgentId = "5a8f78dc-79cf-4af5-b026-868c82405586",
        PharmacyId = "3101283d-cbb5-4667-bc7c-254a4a7f9c88",
        MachineFingerprint = "42142d2f-1b8c-414a-b9a8-1f970bbbfaa7",
        Version = "3.16.0",
        UpdateChannel = "stable",
        SqlServer = "DESKTOP-PIONEER\\PIONEERRX",
        SqlDatabase = "PioneerRx",
        SqlUser = "suavo_ro",
        SqlPassword = "pw_" + SecretMarker + "_PW",
        HmacSalt = "salt_" + SecretMarker + "_SALT",
        CloudCertPin = "pin_" + SecretMarker + "_PIN",
        PricingExecutor = PricingExecutorMode.SqlFirst,
    };

    private static string BuildJson(AgentOptions options, DiagnosticsSnapshotBuilder.SqlDiagnostics sql)
    {
        var snapshot = DiagnosticsSnapshotBuilder.Build(
            options,
            sql,
            new DiagnosticsSnapshotBuilder.WireDiagnostics(
                SentryInitialized: true,
                EventsEmittedTotal: 10,
                WireHandlerFailedTotal: 0,
                SentryEnqueuedTotal: 5,
                SentryEnqueueFailedTotal: 0,
                SentryBeforeSendFailedTotal: 0,
                RulesetVersion: "v1"),
            helper: new { attached = true, consecutiveFailures = 0 },
            watchdog: new { present = false },
            runtimeHealth: new { ok = true },
            commandPipeConnected: false,
            uptimeSeconds: 123,
            processId: 4242,
            collectedAtUtc: new DateTimeOffset(2026, 5, 30, 22, 0, 0, TimeSpan.Zero));

        return JsonSerializer.Serialize(snapshot);
    }

    [Fact]
    public void Build_NeverShipsAnySecret()
    {
        var options = OptionsWithSecrets();
        var json = BuildJson(options, new DiagnosticsSnapshotBuilder.SqlDiagnostics(true, false, options.SqlServer, options.SqlDatabase, options.SqlUser));

        // No secret VALUE leaks (all share the marker).
        Assert.DoesNotContain(SecretMarker, json);
        // No secret KEY surfaces either.
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sqlPassword", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hmacSalt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cloudCertPin", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_IncludesSafeDebuggingFields()
    {
        var options = OptionsWithSecrets();
        var json = BuildJson(options, new DiagnosticsSnapshotBuilder.SqlDiagnostics(true, true, options.SqlServer, options.SqlDatabase, options.SqlUser));

        Assert.Contains("suavo.agent.diagnostics.v1", json);
        Assert.Contains("suavollc.com", json);
        Assert.Contains("SqlFirst", json);                 // pricing executor — the safety-critical mode
        Assert.Contains("DESKTOP-PIONEER", json);          // SQL server wiring (infra, not PHI)
        Assert.Contains("suavo_ro", json);                 // SQL user (infra, not PHI)
        Assert.Contains(options.PharmacyId!, json);
        Assert.Contains("\"mutated\":false", json);        // diagnostics never mutates the box
    }

    [Fact]
    public void Build_SqlNotConfigured_ReportsConfiguredFalse()
    {
        var json = BuildJson(OptionsWithSecrets(),
            new DiagnosticsSnapshotBuilder.SqlDiagnostics(Configured: false, Connected: false, Server: null, Database: null, User: null));

        Assert.Contains("\"configured\":false", json);
        Assert.Contains("\"connected\":false", json);
    }

    [Fact]
    public void Build_SqlConfiguredButDisconnected_ReportsBothStates()
    {
        var options = OptionsWithSecrets();
        var json = BuildJson(options,
            new DiagnosticsSnapshotBuilder.SqlDiagnostics(Configured: true, Connected: false, Server: options.SqlServer, Database: options.SqlDatabase, User: options.SqlUser));

        Assert.Contains("\"configured\":true", json);
        Assert.Contains("\"connected\":false", json);
    }

    [Fact]
    public void Build_SurvivesOutboundPhiGuard()
    {
        var options = OptionsWithSecrets();
        var json = BuildJson(options,
            new DiagnosticsSnapshotBuilder.SqlDiagnostics(true, true, options.SqlServer, options.SqlDatabase, options.SqlUser));

        // The exact guard the real ack POST runs through (PostSignedAsync).
        var ex = Record.Exception(() =>
            OutboundPhiGuard.AssertAllowed("/api/agent/commands/abc/ack", json, options));

        Assert.Null(ex);
    }
}
