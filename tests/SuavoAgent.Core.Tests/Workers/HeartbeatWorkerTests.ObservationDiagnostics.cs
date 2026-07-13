using System.Reflection;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class HeartbeatWorkerTests
{
    // ── Command Dispatch: show_intent_cursor ──

    [Fact]
    public async Task ShowIntentCursor_ValidPayload_RelaysToHelperAndAudits()
    {
        var before = _db.GetAuditEntryCount();
        var response = BuildResponseJson("show_intent_cursor", new
        {
            x = 120.0,
            y = 240.0,
            durationMs = 900,
            commandId = "cmd-cursor-1",
            requesterId = "operator-1"
        });

        await InvokeProcessAsync(response);

        var sent = Assert.Single(_intentCursorClient.Requests);
        Assert.Equal(120.0, sent.X.GetValueOrDefault());
        Assert.Equal(240.0, sent.Y.GetValueOrDefault());
        Assert.Equal(900, sent.DurationMs);
        Assert.True(_db.GetAuditEntryCount() > before);
    }

    [Fact]
    public async Task ShowCursor_CloudCommandAlias_RelaysToHelperAndAudits()
    {
        var before = _db.GetAuditEntryCount();
        var response = BuildResponseJson("show_cursor", new
        {
            x = 120.0,
            y = 240.0,
            durationMs = 900,
            commandId = "cmd-cursor-1",
            requesterId = "operator-1"
        });

        await InvokeProcessAsync(response);

        var sent = Assert.Single(_intentCursorClient.Requests);
        Assert.Equal(120.0, sent.X.GetValueOrDefault());
        Assert.Equal(240.0, sent.Y.GetValueOrDefault());
        Assert.Equal(900, sent.DurationMs);
        Assert.True(_db.GetAuditEntryCount() > before);
    }

    [Fact]
    public async Task ShowIntentCursor_PrimaryCenterAnchor_RelaysWithoutScreenCoordinates()
    {
        var response = BuildResponseJson("show_intent_cursor", new
        {
            anchor = "primary_center",
            durationMs = 900,
            commandId = "cmd-cursor-center",
            requesterId = "operator-1"
        });

        await InvokeProcessAsync(response);

        var sent = Assert.Single(_intentCursorClient.Requests);
        Assert.Null(sent.X);
        Assert.Null(sent.Y);
        Assert.Equal(IntentCursorAnchors.PrimaryCenter, sent.Anchor);
    }

    [Fact]
    public async Task ShowIntentCursor_TextBearingPayload_RejectsBeforeHelper()
    {
        var response = BuildResponseJson("show_intent_cursor", new
        {
            x = 120.0,
            y = 240.0,
            label = "Rx 12345",
            commandId = "cmd-cursor-bad"
        });

        await InvokeProcessAsync(response);

        Assert.Empty(_intentCursorClient.Requests);
    }

    [Fact]
    public async Task ShowIntentCursor_GlidePayload_RelaysToHelperWithTarget()
    {
        // The agentic glide: x,y start + toX,toY target + easing. The PHI safety
        // guard must ALLOW these (toX/toY are numbers; easing is a constrained
        // enum) so the cursor can travel, not just appear. Regression guard for
        // the v3.18.0 miss where the glide shipped but the guard rejected every
        // toX/toY/easing payload before it reached the Helper.
        var response = BuildResponseJson("show_cursor", new
        {
            x = 300.0,
            y = 250.0,
            toX = 1100.0,
            toY = 680.0,
            durationMs = 4500,
            easing = "ease_in_out_cubic",
            tone = "attention",
            commandId = "cmd-glide-1",
            requesterId = "operator-1"
        });

        await InvokeProcessAsync(response);

        var sent = Assert.Single(_intentCursorClient.Requests);
        Assert.Equal(300.0, sent.X.GetValueOrDefault());
        Assert.Equal(1100.0, sent.ToX.GetValueOrDefault());
        Assert.Equal(680.0, sent.ToY.GetValueOrDefault());
        Assert.Equal("ease_in_out_cubic", sent.Easing);
    }

    [Fact]
    public async Task ShowIntentCursor_FreeFormEasing_RejectsBeforeHelper()
    {
        // easing is a new string field — it must stay a closed enum so it cannot
        // become a PHI side-channel. A free-form value is rejected before the Helper.
        var response = BuildResponseJson("show_cursor", new
        {
            x = 300.0,
            y = 250.0,
            toX = 1100.0,
            toY = 680.0,
            easing = "Rx 12345 patient",
            commandId = "cmd-glide-bad"
        });

        await InvokeProcessAsync(response);

        Assert.Empty(_intentCursorClient.Requests);
    }

    [Fact]
    public async Task ShowIntentCursor_UnknownStringPayload_RejectsBeforeHelper()
    {
        var response = BuildResponseJson("show_intent_cursor", new
        {
            x = 120.0,
            y = 240.0,
            note = "look at Rx 12345",
            commandId = "cmd-cursor-unknown"
        });

        await InvokeProcessAsync(response);

        Assert.Empty(_intentCursorClient.Requests);
    }

    [Fact]
    public async Task ShowIntentCursor_UnknownAnchor_RejectsBeforeHelper()
    {
        var response = BuildResponseJson("show_intent_cursor", new
        {
            anchor = "patient_top_left",
            commandId = "cmd-cursor-anchor"
        });

        await InvokeProcessAsync(response);

        Assert.Empty(_intentCursorClient.Requests);
    }

    // ── Command Dispatch: computer-use observe/propose (synthetic, non-PHI) ──

    [Fact]
    public async Task ComputerUseObserve_SyntheticPayload_AuditsWithoutCapturingScreenshots()
    {
        var before = _db.GetAuditEntryCount();
        var response = BuildResponseJson("computer_use_observe", new
        {
            pack = "workstation_health",
            mode = "synthetic",
            commandId = "cmd-cu-observe-1",
            requesterId = "operator-1"
        });

        await InvokeProcessAsync(response);

        Assert.True(_db.GetAuditEntryCount() > before);
        Assert.Empty(_intentCursorClient.Requests);
    }

    [Fact]
    public async Task ComputerUsePropose_SyntheticPayload_AuditsWithoutMutatingPioneerRx()
    {
        var before = _db.GetAuditEntryCount();
        var response = BuildResponseJson("computer_use_propose", new
        {
            pack = "pioneerrx_shadow",
            mode = "synthetic",
            proposal = "run_diagnostics",
            commandId = "cmd-cu-propose-1",
            requesterId = "operator-1"
        });

        await InvokeProcessAsync(response);

        Assert.True(_db.GetAuditEntryCount() > before);
        Assert.Empty(_intentCursorClient.Requests);
    }

    [Fact]
    public async Task ComputerUseObserve_PhiOrInputPayload_RejectsBeforeAudit()
    {
        var before = _db.GetAuditEntryCount();
        var response = BuildResponseJson("computer_use_observe", new
        {
            pack = "workstation_health",
            mode = "synthetic",
            patientName = "Nadim",
            click = true,
            commandId = "cmd-cu-bad"
        });

        await InvokeProcessAsync(response);

        Assert.Equal(before, _db.GetAuditEntryCount());
        Assert.Empty(_intentCursorClient.Requests);
    }

    [Fact]
    public void PricingJobOperationalLogs_DoNotIncludeWorkbookPathTemplates()
    {
        var source = ReadHeartbeatWorkerSource();

        Assert.DoesNotContain("Pricing job {JobId} starting: {Path}", source);
        Assert.DoesNotContain("auto-running pricing job {JobId} on {Path}", source);
    }

    [Fact]
    public void HealthProbe_ReportsNativeMaintenanceState_NotBootstrapScriptState()
    {
        var source = ReadHeartbeatWorkerSource();

        Assert.Contains("maintenanceHostPresent", source);
        Assert.Contains("installStatePresent", source);
        Assert.DoesNotContain("bootstrapPresent", source);
        Assert.DoesNotContain("bootstrapSha256Prefix", source);
        Assert.DoesNotContain("bootstrap.ps1", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectHealthProbe_PhiFreePayload_AuditsWithoutCapturingScreenshots()
    {
        var before = _db.GetAuditEntryCount();
        var response = BuildResponseJson("collect_health_probe", new
        {
            reason = "dashboard_diagnostics",
            commandId = "cmd-health-probe-1",
            requesterId = "operator-1"
        });

        await InvokeProcessAsync(response);

        Assert.True(_db.GetAuditEntryCount() > before);
        Assert.Empty(_intentCursorClient.Requests);
    }

    [Fact]
    public async Task CollectHealthProbe_FreeTextPayload_RejectsBeforeAudit()
    {
        var before = _db.GetAuditEntryCount();
        var response = BuildResponseJson("collect_health_probe", new
        {
            reason = "patient Jane Doe machine froze",
            patientName = "Jane Doe",
            commandId = "cmd-health-probe-bad"
        });

        await InvokeProcessAsync(response);

        Assert.Equal(before, _db.GetAuditEntryCount());
        Assert.Empty(_intentCursorClient.Requests);
    }

    [Fact]
    public async Task ExportPioneerRxShadowFixture_UnsafePayload_RejectsBeforeAudit()
    {
        var before = _db.GetAuditEntryCount();
        var response = BuildResponseJson("export_pioneerrx_shadow_fixture", new
        {
            commandId = "cmd-shadow-bad",
            requesterId = "operator-1",
            patientName = "Jane Doe",
            rxNumber = "123456"
        });

        await InvokeProcessAsync(response);

        Assert.Equal(before, _db.GetAuditEntryCount());
        Assert.Empty(_intentCursorClient.Requests);
    }

    [Fact]
    public async Task ExportPioneerRxShadowFixture_NoSqlConnected_AuditsWithoutFixtureFile()
    {
        var before = _db.GetAuditEntryCount();
        var response = BuildResponseJson("export_pioneerrx_shadow_fixture", new
        {
            commandId = "cmd-shadow-nosql",
            requesterId = "operator-1",
            maxRows = 3,
            includeSyntheticPatientDetails = true
        });

        await InvokeProcessAsync(response);

        Assert.True(_db.GetAuditEntryCount() > before);
        Assert.Empty(_intentCursorClient.Requests);
    }

}
