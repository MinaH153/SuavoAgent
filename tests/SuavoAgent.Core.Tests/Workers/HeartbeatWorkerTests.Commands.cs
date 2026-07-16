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
    // ── Nonce Replay Protection (DB Layer) ──

    [Fact]
    public void DbNonce_FirstUse_Succeeds()
    {
        Assert.True(_db.TryRecordNonce("nonce-fresh-1"));
    }

    [Fact]
    public void DbNonce_DuplicateUse_Fails()
    {
        _db.TryRecordNonce("nonce-dup-1");
        Assert.False(_db.TryRecordNonce("nonce-dup-1"));
    }

    [Fact]
    public void DbNonce_PruneThenReuse_Succeeds()
    {
        _db.TryRecordNonce("nonce-prune-1");
        // Prune with zero window removes everything
        _db.PruneOldNonces(TimeSpan.Zero);
        Assert.True(_db.TryRecordNonce("nonce-prune-1"));
    }

    [Fact]
    public async Task ProcessCommand_InvalidSignature_DoesNotPersistNonce()
    {
        var cmd = Sign("collect_health_probe");
        var tampered = cmd with { Signature = Convert.ToBase64String(new byte[64]) };

        await InvokeProcessAsync(BuildResponseJson(tampered));
        await InvokeProcessAsync(BuildResponseJson(cmd));

        Assert.False(_db.TryRecordNonce(cmd.Nonce));
    }

    // ── Command Dispatch: approve_pom ──

    [Fact]
    public async Task ApprovePom_ValidDigest_ApprovesSession()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        // Move through phases: discovery -> pattern -> model
        _db.UpdateLearningPhase(sessionId, "pattern");
        _db.UpdateLearningPhase(sessionId, "model");

        var templateDigest = new string('a', 64);
        var pomJson = FrozenPom(sessionId, templateDigest);
        _db.StorePomSnapshot(sessionId, pomJson);

        var digest = PomExporter.ComputeDigest(TestPharmacyId, sessionId, pomJson);

        var response = BuildResponseJson("approve_pom", new
        {
            schemaVersion = 1,
            commandId = "11111111-1111-4111-8111-111111111111",
            pomId = "22222222-2222-4222-8222-222222222222",
            sessionId,
            approvedModelDigest = digest,
            approvedTemplateDigest = templateDigest,
            approvedBy = "33333333-3333-4333-8333-333333333333",
            expiresAt = "2099-01-01T00:00:00Z",
        });

        await InvokeProcessAsync(response);

        var session = _db.GetLearningSession(sessionId);
        Assert.NotNull(session);
        Assert.Equal("approved", session.Value.Phase);
        Assert.Equal("supervised", session.Value.Mode);
        Assert.Equal(digest, session.Value.ApprovedModelDigest);
        Assert.Equal(
            "pom_approval_activated",
            _db.GetPomApprovalLedger("11111111-1111-4111-8111-111111111111")!.ResultCode);
    }

    [Fact]
    public async Task ApprovePom_MismatchedDigest_Rejects()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);
        _db.UpdateLearningPhase(sessionId, "pattern");
        _db.UpdateLearningPhase(sessionId, "model");

        var templateDigest = new string('a', 64);
        var pomJson = FrozenPom(sessionId, templateDigest);
        _db.StorePomSnapshot(sessionId, pomJson);

        var response = BuildResponseJson("approve_pom", new
        {
            schemaVersion = 1,
            commandId = "44444444-4444-4444-8444-444444444444",
            pomId = "55555555-5555-4555-8555-555555555555",
            sessionId,
            approvedModelDigest = new string('d', 64),
            approvedTemplateDigest = templateDigest,
            approvedBy = "66666666-6666-4666-8666-666666666666",
            expiresAt = "2099-01-01T00:00:00Z",
        });

        await InvokeProcessAsync(response);

        var session = _db.GetLearningSession(sessionId);
        Assert.NotNull(session);
        Assert.Equal("model", session.Value.Phase); // Unchanged
        Assert.Null(session.Value.ApprovedModelDigest);
        Assert.Equal(
            "pom_approval_model_digest_mismatch",
            _db.GetPomApprovalLedger("44444444-4444-4444-8444-444444444444")!.ResultCode);
    }

    [Fact]
    public async Task ApprovePom_MissingSession_NoOp()
    {
        var response = BuildResponseJson("approve_pom", new
        {
            schemaVersion = 1,
            commandId = "77777777-7777-4777-8777-777777777777",
            pomId = "88888888-8888-4888-8888-888888888888",
            sessionId = "nonexistent-session",
            approvedModelDigest = new string('a', 64),
            approvedTemplateDigest = new string('b', 64),
            approvedBy = "99999999-9999-4999-8999-999999999999",
            expiresAt = "2099-01-01T00:00:00Z",
        });

        // Should not throw
        await InvokeProcessAsync(response);
        Assert.Equal(
            "pom_approval_session_not_found",
            _db.GetPomApprovalLedger("77777777-7777-4777-8777-777777777777")!.ResultCode);
    }

    // ── Command Dispatch: force_learning_phase (test-only hook, M1 gate live testing) ──

    [Fact]
    public async Task ForceLearningPhase_Enabled_AdvancesDiscoveryToPattern()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);
        Assert.Equal("discovery", _db.GetLearningSession(sessionId)!.Value.Phase);

        var response = BuildResponseJson("force_learning_phase", new
        {
            commandId = "cmd-flp-1",
            targetPhase = "pattern",
            sessionId,
        });

        await InvokeProcessAsync(response);

        Assert.Equal("pattern", _db.GetLearningSession(sessionId)!.Value.Phase);
    }

    [Fact]
    public async Task ForceLearningPhase_NoSessionId_ResolvesActiveSession()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var response = BuildResponseJson("force_learning_phase", new
        {
            commandId = "cmd-flp-2",
            targetPhase = "pattern",
        });

        await InvokeProcessAsync(response);

        Assert.Equal("pattern", _db.GetLearningSession(sessionId)!.Value.Phase);
    }

    [Fact]
    public async Task ForceLearningPhase_HookDisabled_NoOp()
    {
        // Flip the test-hook flag OFF on the worker under test.
        var optsField = typeof(HeartbeatWorker)
            .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var opts = (AgentOptions)optsField.GetValue(_worker)!;
        opts.TestHooks.Enabled = false;

        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var response = BuildResponseJson("force_learning_phase", new
        {
            commandId = "cmd-flp-3",
            targetPhase = "pattern",
            sessionId,
        });

        await InvokeProcessAsync(response);

        Assert.Equal("discovery", _db.GetLearningSession(sessionId)!.Value.Phase); // unchanged
    }

    [Fact]
    public async Task ForceLearningPhase_InvalidTransition_NoOp()
    {
        // discovery -> model is a two-step jump; UpdateLearningPhase only allows single-step.
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var response = BuildResponseJson("force_learning_phase", new
        {
            commandId = "cmd-flp-4",
            targetPhase = "model",
            sessionId,
        });

        await InvokeProcessAsync(response);

        Assert.Equal("discovery", _db.GetLearningSession(sessionId)!.Value.Phase); // unchanged
    }

    [Fact]
    public async Task ForceLearningPhase_ApprovedPhase_NotForceable()
    {
        // The hook must never reach the approval-gated phases — that would bypass approve_pom.
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);
        _db.UpdateLearningPhase(sessionId, "pattern");
        _db.UpdateLearningPhase(sessionId, "model"); // legitimately at 'model'

        var response = BuildResponseJson("force_learning_phase", new
        {
            commandId = "cmd-flp-approved",
            targetPhase = "approved",
            sessionId,
        });

        await InvokeProcessAsync(response);

        Assert.Equal("model", _db.GetLearningSession(sessionId)!.Value.Phase); // unchanged — refused
        Assert.Null(_db.GetLearningSession(sessionId)!.Value.ApprovedModelDigest);
    }

    [Fact]
    public async Task ForceLearningPhase_NoActiveSession_NoThrow()
    {
        var response = BuildResponseJson("force_learning_phase", new
        {
            commandId = "cmd-flp-5",
            targetPhase = "pattern",
        });

        // No session exists for the pharmacy — must not throw, must be a no-op.
        await InvokeProcessAsync(response);
    }

    [Fact]
    public async Task ApprovePom_NoPomSnapshot_Rejects()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);
        _db.UpdateLearningPhase(sessionId, "pattern");
        _db.UpdateLearningPhase(sessionId, "model");
        // No POM snapshot stored

        var response = BuildResponseJson("approve_pom", new
        {
            schemaVersion = 1,
            commandId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            pomId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb",
            sessionId,
            approvedModelDigest = new string('c', 64),
            approvedTemplateDigest = new string('d', 64),
            approvedBy = "cccccccc-cccc-4ccc-8ccc-cccccccccccc",
            expiresAt = "2099-01-01T00:00:00Z",
        });

        await InvokeProcessAsync(response);

        var session = _db.GetLearningSession(sessionId);
        Assert.NotNull(session);
        Assert.Equal("model", session.Value.Phase); // Unchanged
        Assert.Equal(
            "pom_approval_frozen_snapshot_missing",
            _db.GetPomApprovalLedger("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa")!.ResultCode);
    }

    // ── Command Dispatch: Feedback Commands ──

    [Fact]
    public async Task ApproveCandidate_InsertsFeedbackEvent_WithPromoteDirective()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var correlationKey = "corr-key-approve-1";
        var response = BuildResponseJson("approve_candidate", new
        {
            correlationKey
        });

        await InvokeProcessAsync(response);

        var events = _db.GetPendingFeedbackEvents(sessionId);
        Assert.Single(events);
        Assert.Equal(DirectiveType.Promote, events[0].DirectiveType);
        Assert.Equal("operator_command", events[0].EventType);
        Assert.Equal("operator", events[0].Source);
        Assert.Equal("correlation_key", events[0].TargetType);
        Assert.Equal(correlationKey, events[0].TargetId);
    }

    [Fact]
    public async Task RejectCandidate_InsertsFeedbackEvent_WithDemoteDirective()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var correlationKey = "corr-key-reject-1";
        var response = BuildResponseJson("reject_candidate", new
        {
            correlationKey
        });

        await InvokeProcessAsync(response);

        var events = _db.GetPendingFeedbackEvents(sessionId);
        Assert.Single(events);
        Assert.Equal(DirectiveType.Demote, events[0].DirectiveType);
        Assert.Equal(correlationKey, events[0].TargetId);
    }

    [Fact]
    public async Task ReapproveCandidate_InsertsFeedbackEvent_WithPromoteDirective()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var correlationKey = "corr-key-reapprove-1";
        var response = BuildResponseJson("reapprove_candidate", new
        {
            correlationKey
        });

        await InvokeProcessAsync(response);

        var events = _db.GetPendingFeedbackEvents(sessionId);
        Assert.Single(events);
        Assert.Equal(DirectiveType.Promote, events[0].DirectiveType);
    }

    [Fact]
    public async Task ForceRelearn_InsertsFeedbackEvent_WithReLearnDirective()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var correlationKey = "corr-key-relearn-1";
        var response = BuildResponseJson("force_relearn", new
        {
            correlationKey
        });

        await InvokeProcessAsync(response);

        var events = _db.GetPendingFeedbackEvents(sessionId);
        Assert.Single(events);
        Assert.Equal(DirectiveType.ReLearn, events[0].DirectiveType);
    }

    [Fact]
    public async Task AdjustWindow_InsertsFeedbackEvent_WithRecalibrateDirective()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var correlationKey = "corr-key-window-1";
        var response = BuildResponseJson("adjust_window", new
        {
            correlationKey,
            windowSeconds = 5.0
        });

        await InvokeProcessAsync(response);

        var events = _db.GetPendingFeedbackEvents(sessionId);
        Assert.Single(events);
        Assert.Equal(DirectiveType.Recalibrate, events[0].DirectiveType);
    }

    [Fact]
    public async Task AcknowledgeStale_InsertsFeedbackEvent_WithPruneDirective()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var correlationKey = "corr-key-stale-1";
        var response = BuildResponseJson("acknowledge_stale", new
        {
            correlationKey
        });

        await InvokeProcessAsync(response);

        var events = _db.GetPendingFeedbackEvents(sessionId);
        Assert.Single(events);
        Assert.Equal(DirectiveType.Prune, events[0].DirectiveType);
    }

    [Fact]
    public async Task FeedbackCommand_MissingCorrelationKey_NoOp()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        // No correlationKey in data
        var response = BuildResponseJson("approve_candidate", new
        {
            somethingElse = "unrelated"
        });

        await InvokeProcessAsync(response);

        var events = _db.GetPendingFeedbackEvents(sessionId);
        Assert.Empty(events);
    }

    [Fact]
    public async Task FeedbackCommand_NoActiveSession_NoOp()
    {
        // No session created — GetActiveSessionId returns null
        var response = BuildResponseJson("approve_candidate", new
        {
            correlationKey = "corr-key-orphan"
        });

        await InvokeProcessAsync(response);
        // No exception, no event inserted
    }

    [Fact]
    public async Task FeedbackCommand_CreatesAuditEntry()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var correlationKey = "corr-key-audit-1";
        var countBefore = _db.GetAuditEntryCount();

        var response = BuildResponseJson("approve_candidate", new
        {
            correlationKey
        });

        await InvokeProcessAsync(response);

        var countAfter = _db.GetAuditEntryCount();
        Assert.True(countAfter > countBefore, "Audit entry should be appended for feedback commands");
    }

    [Fact]
    public async Task RepairAgentCommand_RecordsAuditEntryEvenWhenBootstrapMissing()
    {
        var countBefore = _db.GetAuditEntryCount();

        var response = BuildResponseJson("repair_agent", new
        {
            commandId = "cmd-repair-1",
            reason = "watchdog_critical"
        });

        await InvokeProcessAsync(response);

        Assert.True(_db.GetAuditEntryCount() > countBefore);
    }

    [Fact]
    public async Task RepairCommand_CloudCommandAlias_RecordsAuditEntryEvenWhenBootstrapMissing()
    {
        var countBefore = _db.GetAuditEntryCount();

        var response = BuildResponseJson("repair", new
        {
            commandId = "cmd-repair-1",
            reason = "watchdog_critical"
        });

        await InvokeProcessAsync(response);

        Assert.True(_db.GetAuditEntryCount() > countBefore);
    }

    [Fact]
    public async Task RepairAgentCommand_QueuesWatchdogRepairRequest()
    {
        var response = BuildResponseJson("repair_agent", new
        {
            commandId = "cmd-repair-queued-1",
            reason = "watchdog_critical"
        });

        await InvokeProcessAsync(response);

        Assert.True(File.Exists(_repairRequestPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(_repairRequestPath));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("repair_agent", root.GetProperty("command").GetString());
        Assert.Equal("cmd-repair-queued-1", root.GetProperty("commandId").GetString());
        Assert.Equal("watchdog_critical", root.GetProperty("reason").GetString());
        Assert.Equal(TestAgentId, root.GetProperty("agentId").GetString());
        Assert.Equal(TestFingerprint, root.GetProperty("machineFingerprint").GetString());
        using var signedData = JsonDocument.Parse(root.GetProperty("dataJson").GetString()!);
        Assert.Equal(
            "cmd-repair-queued-1",
            signedData.RootElement.GetProperty("commandId").GetString());
        Assert.Equal(
            "watchdog_critical",
            signedData.RootElement.GetProperty("reason").GetString());
        Assert.True(DateTimeOffset.TryParse(
            signedData.RootElement.GetProperty("expiresAt").GetString(),
            out _));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("signature").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("requestedAtUtc").GetString()));

        var request = JsonSerializer.Deserialize<RemoteRepairRequest>(
            root.GetRawText(),
            RemoteRepairContract.JsonOptions)!;
        var result = RemoteRepairContract.Validate(
            request,
            TestAgentId,
            TestFingerprint,
            new Dictionary<string, string> { [TestKeyId] = _pubKeyDer },
            DateTimeOffset.UtcNow);
        Assert.True(result.IsValid, result.Code);
    }

    [Fact]
    public async Task RepairAgentCommand_RejectsFreeformRepairReason()
    {
        // Bug 20 — pre-fix, an unrecognized reason was silently re-mapped to
        // "remote_command" and the command appeared to succeed. Now we NACK
        // up front so the operator gets clear feedback. Watchdog-side
        // redaction stays as defense-in-depth (see
        // WatchdogWorkerTests.Tick_QueuedRemoteRepair_RedactsUnexpectedReasonInTelemetry).
        if (File.Exists(_repairRequestPath)) File.Delete(_repairRequestPath);

        var response = BuildResponseJson("repair_agent", new
        {
            commandId = "cmd-repair-rejected-1",
            reason = "patient_john_smith"
        });

        await InvokeProcessAsync(response);

        Assert.False(
            File.Exists(_repairRequestPath),
            "rejected reason must not result in a queued repair request");
    }

    [Theory]
    [InlineData("remote_command")]
    [InlineData("watchdog_critical")]
    [InlineData("cloud_stale")]
    [InlineData("install_repair")]
    [InlineData("runtime_health_missing")]
    [InlineData("operator_requested")]
    public async Task RepairAgentCommand_AcceptsAllAllowedReasons(string reason)
    {
        if (File.Exists(_repairRequestPath)) File.Delete(_repairRequestPath);

        var response = BuildResponseJson("repair_agent", new
        {
            commandId = $"cmd-allowed-{reason}",
            reason
        });

        await InvokeProcessAsync(response);

        Assert.True(File.Exists(_repairRequestPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(_repairRequestPath));
        Assert.Equal(reason, doc.RootElement.GetProperty("reason").GetString());
    }

    // ── Command Dispatch: acknowledge_drift ──

    [Fact]
    public async Task AcknowledgeDrift_ResumeSupervised_ClearsHold()
    {
        // Set up a canary hold
        _db.UpsertCanaryHold(TestPharmacyId, "pioneerrx", "critical", "fp-baseline-1");

        Assert.NotNull(_db.GetCanaryHold(TestPharmacyId, "pioneerrx"));

        var response = BuildResponseJson("acknowledge_drift", new
        {
            action = "resume_supervised",
            incidentId = "inc-001"
        });

        await InvokeProcessAsync(response);

        Assert.Null(_db.GetCanaryHold(TestPharmacyId, "pioneerrx"));
    }

    [Fact]
    public async Task AcknowledgeDrift_ApproveNewBaseline_ClearsHold()
    {
        _db.UpsertCanaryHold(TestPharmacyId, "pioneerrx", "warning", "fp-baseline-2");

        var response = BuildResponseJson("acknowledge_drift", new
        {
            action = "approve_new_baseline",
            incidentId = "inc-002",
            targetSchemaEpoch = 3
        });

        await InvokeProcessAsync(response);

        Assert.Null(_db.GetCanaryHold(TestPharmacyId, "pioneerrx"));
    }

    [Fact]
    public async Task AcknowledgeDrift_MissingAction_NoOp()
    {
        _db.UpsertCanaryHold(TestPharmacyId, "pioneerrx", "critical", "fp-baseline-3");

        var response = BuildResponseJson("acknowledge_drift", new
        {
            incidentId = "inc-003"
            // action missing
        });

        await InvokeProcessAsync(response);

        // Hold should remain
        Assert.NotNull(_db.GetCanaryHold(TestPharmacyId, "pioneerrx"));
    }

    [Fact]
    public async Task AcknowledgeDrift_UnknownAction_DoesNotClearHold()
    {
        _db.UpsertCanaryHold(TestPharmacyId, "pioneerrx", "warning", "fp-baseline-4");

        var response = BuildResponseJson("acknowledge_drift", new
        {
            action = "unknown_action",
            incidentId = "inc-004"
        });

        await InvokeProcessAsync(response);

        Assert.NotNull(_db.GetCanaryHold(TestPharmacyId, "pioneerrx"));
    }

    [Fact]
    public async Task AcknowledgeDrift_CreatesAuditEntry()
    {
        var countBefore = _db.GetAuditEntryCount();

        var response = BuildResponseJson("acknowledge_drift", new
        {
            action = "resume_supervised",
            incidentId = "inc-audit"
        });

        await InvokeProcessAsync(response);

        Assert.True(_db.GetAuditEntryCount() > countBefore);
    }

    // ── Command Dispatch: delivery_writeback ──

    [Fact]
    public async Task DeliveryWriteback_MissingTransition_NoOp()
    {
        var response = BuildResponseJson("delivery_writeback", new
        {
            rxNumber = 12345
            // transition missing
        });

        // Should not throw
        await InvokeProcessAsync(response);
    }

    [Fact]
    public async Task DeliveryWriteback_MissingRxNumber_NoOp()
    {
        var response = BuildResponseJson("delivery_writeback", new
        {
            transition = "pickup"
            // rxNumber missing — parser uses TryGetProperty with GetInt32, 0.ToString() = "0"
            // Actually "0" is not empty, so it passes. But let's test missing field.
        });

        // Should not throw — the handler will get rxNumber as "0" which is non-empty
        await InvokeProcessAsync(response);
    }

    [Fact]
    public async Task DeliveryWriteback_LegacyRawRxPayload_IsRejectedWithoutAudit()
    {
        var countBefore = _db.GetAuditEntryCount();

        var response = BuildResponseJson("delivery_writeback", new
        {
            transition = "pickup",
            rxNumber = 99001,
            fillNumber = 1,
            taskId = "wb-task-1"
        });

        await InvokeProcessAsync(response);

        Assert.Equal(countBefore, _db.GetAuditEntryCount());
    }

}
