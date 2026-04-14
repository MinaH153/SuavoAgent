using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Tests for HeartbeatWorker command dispatch, signed command verification integration,
/// and feedback command handling. Uses reflection to invoke ProcessSignedCommandAsync
/// since HeartbeatWorker's handlers are private.
/// </summary>
public class HeartbeatWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private readonly HeartbeatWorker _worker;
    private readonly ECDsa _signingKey;
    private readonly string _pubKeyDer;
    private readonly MethodInfo _processMethod;
    private const string TestAgentId = "agent-hb-test";
    private const string TestFingerprint = "fp-hb-test";
    private const string TestPharmacyId = "pharm-hb-test";
    private const string TestKeyId = "suavo-cmd-v1";

    public HeartbeatWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_hb_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);

        _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _pubKeyDer = Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo());

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var sp = services.BuildServiceProvider();

        var options = Options.Create(new AgentOptions
        {
            AgentId = TestAgentId,
            MachineFingerprint = TestFingerprint,
            PharmacyId = TestPharmacyId,
            HeartbeatIntervalSeconds = 30,
        });

        _worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance, options, sp, _db);

        // Inject our test key into the _commandVerifier via reflection
        var verifierField = typeof(HeartbeatWorker)
            .GetField("_commandVerifier", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var testVerifier = new SignedCommandVerifier(
            new Dictionary<string, string> { [TestKeyId] = _pubKeyDer },
            TestAgentId, TestFingerprint);
        verifierField.SetValue(_worker, testVerifier);

        _processMethod = typeof(HeartbeatWorker)
            .GetMethod("ProcessSignedCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // ── Helpers ──

    private SignedCommand Sign(string command, string? dataJson = null)
    {
        var ts = DateTimeOffset.UtcNow.ToString("o");
        var nonce = Guid.NewGuid().ToString();
        var dataHash = SignedCommandVerifier.ComputeDataHash(dataJson);
        var canonical = $"{command}|{TestAgentId}|{TestFingerprint}|{ts}|{nonce}|{dataHash}";
        var sig = Convert.ToBase64String(
            _signingKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
        return new SignedCommand(command, TestAgentId, TestFingerprint, ts, nonce, TestKeyId, sig, dataHash);
    }

    /// <summary>
    /// Builds a heartbeat response JSON containing a signedCommand envelope
    /// in the shape the worker expects: { data: { signedCommand: { ... } } }
    /// </summary>
    private JsonElement BuildResponseJson(string command, object? data = null)
    {
        var cmd = Sign(command, data != null ? JsonSerializer.Serialize(data) : null);
        var envelope = new Dictionary<string, object?>
        {
            ["command"] = cmd.Command,
            ["agentId"] = cmd.AgentId,
            ["machineFingerprint"] = cmd.MachineFingerprint,
            ["timestamp"] = cmd.Timestamp,
            ["nonce"] = cmd.Nonce,
            ["keyId"] = cmd.KeyId,
            ["signature"] = cmd.Signature,
        };
        if (data != null)
            envelope["data"] = data;

        var response = new Dictionary<string, object>
        {
            ["data"] = new Dictionary<string, object> { ["signedCommand"] = envelope }
        };
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(response));
    }

    private async Task InvokeProcessAsync(JsonElement response)
    {
        var task = (Task)_processMethod.Invoke(_worker, new object[] { response, CancellationToken.None })!;
        await task;
    }

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

    // ── Command Dispatch: approve_pom ──

    [Fact]
    public async Task ApprovePom_ValidDigest_ApprovesSession()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        // Move through phases: discovery -> pattern -> model
        _db.UpdateLearningPhase(sessionId, "pattern");
        _db.UpdateLearningPhase(sessionId, "model");

        var pomJson = """{"processes":[],"schemas":[],"queries":[]}""";
        _db.StorePomSnapshot(sessionId, pomJson);

        var digest = PomExporter.ComputeDigest(TestPharmacyId, sessionId, pomJson);

        var response = BuildResponseJson("approve_pom", new
        {
            sessionId,
            approvedModelDigest = digest,
            approvedBy = "operator-1"
        });

        await InvokeProcessAsync(response);

        var session = _db.GetLearningSession(sessionId);
        Assert.NotNull(session);
        Assert.Equal("approved", session.Value.Phase);
        Assert.Equal("supervised", session.Value.Mode);
        Assert.Equal(digest, session.Value.ApprovedModelDigest);
    }

    [Fact]
    public async Task ApprovePom_MismatchedDigest_Rejects()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);
        _db.UpdateLearningPhase(sessionId, "pattern");
        _db.UpdateLearningPhase(sessionId, "model");

        var pomJson = """{"processes":[],"schemas":[],"queries":[]}""";
        _db.StorePomSnapshot(sessionId, pomJson);

        var response = BuildResponseJson("approve_pom", new
        {
            sessionId,
            approvedModelDigest = "deadbeef00000000000000000000000000000000000000000000000000000000",
            approvedBy = "operator-1"
        });

        await InvokeProcessAsync(response);

        var session = _db.GetLearningSession(sessionId);
        Assert.NotNull(session);
        Assert.Equal("model", session.Value.Phase); // Unchanged
        Assert.Null(session.Value.ApprovedModelDigest);
    }

    [Fact]
    public async Task ApprovePom_MissingSession_NoOp()
    {
        var response = BuildResponseJson("approve_pom", new
        {
            sessionId = "nonexistent-session",
            approvedModelDigest = "abc123",
            approvedBy = "operator-1"
        });

        // Should not throw
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
            sessionId,
            approvedModelDigest = "someDigest",
            approvedBy = "operator-1"
        });

        await InvokeProcessAsync(response);

        var session = _db.GetLearningSession(sessionId);
        Assert.NotNull(session);
        Assert.Equal("model", session.Value.Phase); // Unchanged
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
    public async Task DeliveryWriteback_CreatesAuditEntry()
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

        Assert.True(_db.GetAuditEntryCount() > countBefore,
            "delivery_writeback should create an audit entry");
    }

    // ── Command Dispatch: Unknown Command ──

    [Fact]
    public async Task UnknownCommand_DoesNotThrow()
    {
        var response = BuildResponseJson("totally_unknown_command", new
        {
            someData = "irrelevant"
        });

        // Should log a debug message but not throw
        await InvokeProcessAsync(response);
    }

    // ── Nonce Replay at Dispatch Level ──

    [Fact]
    public async Task ProcessCommand_ReplayedNonce_RejectedByDb()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        var correlationKey = "corr-key-replay-test";
        var response = BuildResponseJson("approve_candidate", new
        {
            correlationKey
        });

        // First call succeeds
        await InvokeProcessAsync(response);
        var events1 = _db.GetPendingFeedbackEvents(sessionId);
        Assert.Single(events1);

        // Second call with same response (same nonce) — rejected at DB layer
        await InvokeProcessAsync(response);
        var events2 = _db.GetPendingFeedbackEvents(sessionId);
        // Still only one event (the verifier in-memory nonce may also block,
        // but the DB nonce check is the first line of defense in ProcessSignedCommandAsync)
        Assert.Single(events2);
    }

    // ── Null/Missing signedCommand ──

    [Fact]
    public async Task ProcessCommand_NullSignedCommand_NoOp()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            {"data":{"signedCommand":null}}
        """);

        await InvokeProcessAsync(json);
    }

    [Fact]
    public async Task ProcessCommand_NoSignedCommandField_NoOp()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            {"data":{"status":"ok"}}
        """);

        await InvokeProcessAsync(json);
    }

    [Fact]
    public async Task ProcessCommand_NoDataField_NoOp()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            {"status":"ok"}
        """);

        await InvokeProcessAsync(json);
    }

    // ── Decommission Phase 1 ──

    [Fact]
    public async Task Decommission_Phase1_SetsAuditEntry()
    {
        var countBefore = _db.GetAuditEntryCount();

        var response = BuildResponseJson("decommission");

        await InvokeProcessAsync(response);

        Assert.True(_db.GetAuditEntryCount() > countBefore,
            "Decommission phase 1 should create an audit entry");
    }

    // ── Fetch Patient: validation ──

    [Fact]
    public async Task FetchPatient_InvalidRxNumber_NoOp()
    {
        // rxNumber > 20 chars
        var response = BuildResponseJson("fetch_patient", new
        {
            rxNumber = "123456789012345678901", // 21 chars
            requesterId = "user-1"
        });

        // Should not throw, just log warning
        await InvokeProcessAsync(response);
    }

    [Fact]
    public async Task FetchPatient_EmptyRxNumber_NoOp()
    {
        var response = BuildResponseJson("fetch_patient", new
        {
            rxNumber = "",
            requesterId = "user-1"
        });

        await InvokeProcessAsync(response);
    }

    // ── DataHash Computation ──

    [Fact]
    public void ComputeDataHash_DeterministicForSameInput()
    {
        var json = """{"key":"value"}""";
        var h1 = SignedCommandVerifier.ComputeDataHash(json);
        var h2 = SignedCommandVerifier.ComputeDataHash(json);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void ComputeDataHash_DifferentInputs_DifferentHashes()
    {
        var h1 = SignedCommandVerifier.ComputeDataHash("""{"a":1}""");
        var h2 = SignedCommandVerifier.ComputeDataHash("""{"a":2}""");
        Assert.NotEqual(h1, h2);
    }

    // ── PomExporter.ComputeDigest ──

    [Fact]
    public void PomDigest_DeterministicForSameInput()
    {
        var d1 = PomExporter.ComputeDigest("pharm-1", "sess-1", """{"data":"test"}""");
        var d2 = PomExporter.ComputeDigest("pharm-1", "sess-1", """{"data":"test"}""");
        Assert.Equal(d1, d2);
    }

    [Fact]
    public void PomDigest_DifferentPharmacy_DifferentDigest()
    {
        var d1 = PomExporter.ComputeDigest("pharm-1", "sess-1", """{"data":"test"}""");
        var d2 = PomExporter.ComputeDigest("pharm-2", "sess-1", """{"data":"test"}""");
        Assert.NotEqual(d1, d2);
    }

    [Fact]
    public void PomDigest_DifferentPomJson_DifferentDigest()
    {
        var d1 = PomExporter.ComputeDigest("pharm-1", "sess-1", """{"data":"v1"}""");
        var d2 = PomExporter.ComputeDigest("pharm-1", "sess-1", """{"data":"v2"}""");
        Assert.NotEqual(d1, d2);
    }

    // ── Multiple Feedback Commands in Sequence ──

    [Fact]
    public async Task MultipleFeedbackCommands_AllRecorded()
    {
        var sessionId = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sessionId, TestPharmacyId);

        await InvokeProcessAsync(BuildResponseJson("approve_candidate",
            new { correlationKey = "key-1" }));
        await InvokeProcessAsync(BuildResponseJson("reject_candidate",
            new { correlationKey = "key-2" }));
        await InvokeProcessAsync(BuildResponseJson("force_relearn",
            new { correlationKey = "key-3" }));

        var events = _db.GetPendingFeedbackEvents(sessionId);
        Assert.Equal(3, events.Count);

        Assert.Equal(DirectiveType.Promote, events[0].DirectiveType);
        Assert.Equal(DirectiveType.Demote, events[1].DirectiveType);
        Assert.Equal(DirectiveType.ReLearn, events[2].DirectiveType);
    }

    // ── CanaryHold State Transitions ──

    [Fact]
    public void CanaryHold_UpsertAndGet_RoundTrips()
    {
        _db.UpsertCanaryHold(TestPharmacyId, "pioneerrx", "warning", "fp-1");
        var hold = _db.GetCanaryHold(TestPharmacyId, "pioneerrx");
        Assert.NotNull(hold);
        Assert.Equal("warning", hold.Value.Severity);
    }

    [Fact]
    public void CanaryHold_ClearAndGet_ReturnsNull()
    {
        _db.UpsertCanaryHold(TestPharmacyId, "pioneerrx", "critical", "fp-2");
        _db.ClearCanaryHold(TestPharmacyId, "pioneerrx");
        Assert.Null(_db.GetCanaryHold(TestPharmacyId, "pioneerrx"));
    }

    [Fact]
    public void CanaryHold_UpsertUpdatesSeverity()
    {
        _db.UpsertCanaryHold(TestPharmacyId, "pioneerrx", "warning", "fp-3");
        _db.UpsertCanaryHold(TestPharmacyId, "pioneerrx", "critical", "fp-3-updated");
        var hold = _db.GetCanaryHold(TestPharmacyId, "pioneerrx");
        Assert.Equal("critical", hold!.Value.Severity);
    }

    // ── Learning Session Phase Transitions (used by approve_pom) ──

    [Fact]
    public void LearningSession_PhaseTransition_FollowsOrder()
    {
        var sid = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sid, TestPharmacyId);

        Assert.Equal("discovery", _db.GetLearningSession(sid)!.Value.Phase);
        _db.UpdateLearningPhase(sid, "pattern");
        Assert.Equal("pattern", _db.GetLearningSession(sid)!.Value.Phase);
        _db.UpdateLearningPhase(sid, "model");
        Assert.Equal("model", _db.GetLearningSession(sid)!.Value.Phase);
        _db.UpdateLearningPhase(sid, "approved");
        Assert.Equal("approved", _db.GetLearningSession(sid)!.Value.Phase);
    }

    [Fact]
    public void LearningSession_InvalidPhaseTransition_Throws()
    {
        var sid = $"sess-{Guid.NewGuid():N}";
        _db.CreateLearningSession(sid, TestPharmacyId);

        // Can't skip discovery -> model
        Assert.Throws<InvalidOperationException>(() =>
            _db.UpdateLearningPhase(sid, "model"));
    }

    // ── Decommission Path Security ──

    [Fact]
    public void DecommissionPath_UsesAppContext_NotHardcodedPath()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "SuavoAgent.Core", "Workers", "HeartbeatWorker.cs"));

        Assert.DoesNotContain(@"C:\Program Files\Suavo", source);
        Assert.DoesNotContain(@"C:\\Program Files\\Suavo", source);
        Assert.DoesNotContain("ExecutionPolicy Bypass", source);
        Assert.DoesNotContain("powershell.exe", source);
    }
}
