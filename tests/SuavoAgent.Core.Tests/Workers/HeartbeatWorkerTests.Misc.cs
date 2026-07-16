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
using SuavoAgent.Contracts.Security;
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

    [Fact]
    public async Task ExtendAppAllowlist_RemoteCommandCannotWidenSandbox()
    {
        ActuationAllowlistedSandboxApps.ExtendAllowlist(null);
        try
        {
            var response = BuildResponseJson("extend_app_allowlist", new
            {
                commandId = "cmd-remote-allowlist",
                apps = new Dictionary<string, string>
                {
                    ["mspaint"] = "mspaint.exe",
                },
            });

            await InvokeProcessAsync(response);

            Assert.False(ActuationAllowlistedSandboxApps.ProcessNames.ContainsKey("mspaint"));
        }
        finally
        {
            ActuationAllowlistedSandboxApps.ExtendAllowlist(null);
        }
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

    // ── Retired decommission ──

    [Fact]
    public async Task Decommission_LegacyCommand_PerformsNoLocalStateMutation()
    {
        var countBefore = _db.GetAuditEntryCount();

        var response = BuildResponseJson("decommission");

        await InvokeProcessAsync(response);

        Assert.Equal(countBefore, _db.GetAuditEntryCount());
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
    public void DecommissionCommand_IsRetiredAndContainsNoMutationSurface()
    {
        var source = ReadHeartbeatWorkerSource();
        var start = source.IndexOf("private async Task HandleRetiredDecommissionAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<bool> HandleSelfUninstallAsync", StringComparison.Ordinal);
        Assert.NotEqual(-1, start);
        Assert.True(end > start);
        var decommissionSource = source[start..end];

        Assert.Contains("command_retired_use_self_uninstall", decommissionSource);
        Assert.DoesNotContain("Process.Start", decommissionSource);
        Assert.DoesNotContain("Directory.Delete", decommissionSource);
        Assert.DoesNotContain("SecureDelete", decommissionSource);
        Assert.DoesNotContain("SetConfigValue", decommissionSource);
        Assert.DoesNotContain("AppendChainedAuditEntry", decommissionSource);
    }

    [Fact]
    public void HeartbeatPayload_DoesNotReportSqlAsPioneerRxStatus()
    {
        var source = ReadHeartbeatWorkerSource();

        Assert.DoesNotContain("pioneerrxStatus = sqlConnected", source);
        Assert.Contains("pioneerRxObservation", source);
    }

    [Fact]
    public void HeartbeatPayload_SurfacesVisionAndWatchdogHealthSignals()
    {
        var source = ReadHeartbeatWorkerSource();

        Assert.Contains("periodicCaptureEnabled", source);
        Assert.Contains("VisionCaptureTelemetry", source);
        Assert.Contains("watchdog = BuildWatchdogPayload()", source);
        Assert.Contains("watchdog-health.json", source);
    }

    [Fact]
    public void HeartbeatPayload_RecordsLastSuccessfulCloudSync()
    {
        var source = ReadHeartbeatWorkerSource();

        Assert.Contains("lastSyncAt = _lastSyncAt?.ToString(\"o\")", source);
        Assert.Contains("_lastSyncAt = heartbeatObservedAt;", source);
    }

    [Fact]
    public void HeartbeatPayload_CountsConsecutiveHelperAttachmentFailures()
    {
        var source = ReadHeartbeatWorkerSource();

        Assert.Contains("_helperConsecutiveFailures = helperAttached ? 0 : _helperConsecutiveFailures + 1", source);
        Assert.Contains("consecutiveFailures = _helperConsecutiveFailures", source);
    }

    private static string ReadHeartbeatWorkerSource()
    {
        var workers = Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "src",
            "SuavoAgent.Core",
            "Workers");

        return string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(workers, "HeartbeatWorker*.cs")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));
    }

    private sealed class FakeIntentCursorClient : IIntentCursorClient
    {
        public List<IntentCursorRequest> Requests { get; } = new();

        public Task<IntentCursorClientResult> ShowAsync(IntentCursorRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new IntentCursorClientResult(
                true,
                null,
                new IntentCursorResponse(
                    true,
                    IntentCursorCoordinateSpaces.Screen,
                    request.DurationMs,
                    request.DiameterPx,
                    request.Tone)));
        }
    }

    private sealed class FakePricingJobExecutor : IPricingJobExecutor
    {
        public List<PricingJobSpec> Specs { get; } = new();
        public bool BlockUntilCancellation { get; set; }
        public bool CancellationObserved { get; private set; }
        public Exception? Failure { get; set; }
        public AgentStateDb? PersistCompletedResultTo { get; set; }
        public Action<PricingJobSpec>? BeforePersistCompletedResult { get; init; }
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PricingJobExecutionResult> RunAsync(PricingJobSpec spec, CancellationToken ct)
        {
            Specs.Add(spec);
            Started.TrySetResult(true);
            if (Failure is not null) throw Failure;
            if (BlockUntilCancellation)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    CancellationObserved = true;
                    throw;
                }
            }
            var progress = new PricingJobProgress(spec.JobId, 1, 1, 0, PricingJobStatus.Completed);
            if (PersistCompletedResultTo is not null)
            {
                PersistCompletedResultTo.UpsertPricingJob(
                    spec, PricingJobStatus.Running, 1, 0, 0);
                BeforePersistCompletedResult?.Invoke(spec);
                PersistCompletedResultTo.SavePricingResult(new SupplierPriceResult(
                    spec.JobId,
                    2,
                    "55111064501",
                    true,
                    "supplier",
                    1.25m,
                    null));
                PersistCompletedResultTo.UpsertPricingJob(
                    spec, PricingJobStatus.Completed, 1, 1, 0);
            }
            return new PricingJobExecutionResult(progress, "sql", true, null);
        }
    }

    private sealed class FakeTopDispensedWorklistBuilder
        : ITopDispensedWorklistBuilder
    {
        public TopDispensedWorklistBuildResult Result { get; set; } =
            TopDispensedWorklistBuildResult.Fail(
                "pricing_worklist_source_unavailable");
        public List<string> CommandIds { get; } = new();

        public Task<TopDispensedWorklistBuildResult> BuildAsync(
            string commandId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommandIds.Add(commandId);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeHeartbeatIpcCommandClient : IIpcCommandClient
    {
        public bool IsConnected => true;

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(
            IpcRequest request,
            TimeSpan timeout,
            CancellationToken ct)
        {
            var data = JsonSerializer.SerializeToElement(
                new HelperPingInfo(1234, 1, 1, true));
            return Task.FromResult<IpcResponse?>(new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                data,
                null));
        }
    }

    private sealed class CancellationBlockingPostSigner : IPostSigner
    {
        public string? BoundAgentInstanceId =>
            "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        public string? BoundPharmacyId =>
            "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<JsonElement?> PostSignedAsync(
            string path,
            object payload,
            CancellationToken ct) => Task.FromResult<JsonElement?>(null);

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) => Task.FromResult<JsonElement?>(null);

        public async Task<VerifiedCloudPostResponse?>
            PostSignedResponseVerifiedAsync(
                string path,
                object payload,
                CancellationToken ct)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }

    private sealed class ExpireAtDetachedDispatchTimeProvider(
        DateTimeOffset acceptedAt) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _reads) <= 3
                ? acceptedAt
                : acceptedAt.AddSeconds(2);
    }

    private sealed class TestAutonomyDeviceSigner : IDeviceAuthoritySigner
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

    private sealed class TestPioneerRxAutonomyIdentityProvider :
        IPioneerRxAutonomyIdentityProvider
    {
        public PioneerRxAutonomyIdentity? Current(DateTimeOffset now) => new(
            "1.2.3.4",
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            new string('e', 64),
            7);
    }

    private sealed class RecordingAckHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"success\":true}"),
            };
        }
    }

    private sealed class FakePomRegistry : IActivePmsAdapterRegistry
    {
        public AdapterActivationResult ActivateApproved(string sessionId) =>
            new(AdapterActivationOutcome.Activated, "approved_exact_binding");

        public ActivePmsAdapterLease? TryAcquire(DateTimeOffset now) => null;

        public void ReportHealthy(ActivePmsAdapterBinding binding, DateTimeOffset now) { }

        public void ReportUnhealthy(
            ActivePmsAdapterBinding binding,
            DateTimeOffset now,
            string errorCategory) { }

        public ActivePmsAdapterStatus Snapshot(DateTimeOffset now) =>
            new(false, null, null, 0, null, null);
    }
}
