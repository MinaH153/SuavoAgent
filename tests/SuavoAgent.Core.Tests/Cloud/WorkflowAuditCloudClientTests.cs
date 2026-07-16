using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class WorkflowAuditCloudClientTests : IDisposable
{
    private static readonly string AgentId = Guid.NewGuid().ToString("D");
    private static readonly string PharmacyId = Guid.NewGuid().ToString("D");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"suavo-workflow-audit-{Guid.NewGuid():N}");

    public WorkflowAuditCloudClientTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task DurablePayload_IsExactPhiNegativeAndOrdinalIsContiguous()
    {
        using var db = OpenDb();
        var client = CreateClient(db, new ReceiptSigner(AgentId, PharmacyId));
        var runId = Guid.NewGuid().ToString("D");
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        await client.PostStepAuditAsync(
            Event(firstId, runId, 4, "pioneerrx_query", "success"),
            CancellationToken.None);
        await client.PostStepAuditAsync(
            Event(secondId, runId, 4, "pioneerrx_query", "failed"),
            CancellationToken.None);

        var events = db.GetWorkflowAuditEvents(runId);
        Assert.Equal([0, 1], events.Select(entry => entry.ExecutionOrdinal));
        Assert.Equal([firstId, secondId], events.Select(entry => entry.EventId));
        foreach (var entry in events)
        {
            using var document = JsonDocument.Parse(entry.PayloadJson);
            var names = document.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(14, names.Count);
            Assert.True(names.SetEquals(new[]
            {
                "schemaVersion", "eventId", "executionOrdinal", "stepIndex",
                "verbName", "verbVersion", "requestedDryRun",
                "effectiveDryRun", "outcome", "execDurationMs", "errorKind",
                "paramsFieldCount", "beforeStateFieldCount",
                "afterStateFieldCount",
            }));
            Assert.DoesNotContain("params\"", entry.PayloadJson, StringComparison.Ordinal);
            Assert.DoesNotContain("state\"", entry.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("detail", entry.PayloadJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(Sha256(entry.PayloadJson), entry.PayloadSha256);
        }
    }

    [Fact]
    public void ReceiptDigests_MatchCrossLanguageRfc8785GoldenVectors()
    {
        const string workflowRunId =
            "22222222-2222-4222-8222-222222222222";
        const string agentId =
            "33333333-3333-4333-8333-333333333333";
        const string pharmacyId =
            "44444444-4444-4444-8444-444444444444";
        var eventId = Guid.Parse("11111111-1111-4111-8111-111111111111");

        Assert.Equal(
            "713e1a05d2c4b255674f2381a4c6e06d1f21b784af9d1becb010f56609dc8eed",
            WorkflowAuditCloudClient.ComputeAuditReceiptDigest(
                workflowRunId,
                agentId,
                pharmacyId,
                eventId,
                7,
                2,
                "press_keys",
                "1.0.0",
                false,
                true,
                "failed",
                123,
                "execution_failed",
                2,
                null,
                3));

        Assert.Equal(
            "73dbd673b0c01a2cf8696e47924c5c501354bbb974529d34c018d99081109431",
            WorkflowAuditCloudClient.ComputeCompletionReceiptDigest(
                workflowRunId,
                agentId,
                pharmacyId,
                Guid.Parse("55555555-5555-4555-8555-555555555555"),
                8,
                eventId,
                new string('a', 64),
                "aborted",
                "cycle_limit_exceeded"));
    }

    [Fact]
    public async Task Flush_AcceptsEventsInOrderThenSendsExactCompletionDigest()
    {
        using var db = OpenDb();
        var signer = new ReceiptSigner(AgentId, PharmacyId);
        var client = CreateClient(db, signer);
        var runId = Guid.NewGuid().ToString("D");
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await client.PostStepAuditAsync(
            Event(firstId, runId, 1, "pioneerrx_query", "success"),
            CancellationToken.None);
        await client.PostStepAuditAsync(
            Event(secondId, runId, 2, "assert_element", "skipped"),
            CancellationToken.None);
        await client.PostRunCompletedAsync(
            runId, WorkflowRunOutcome.Completed, null, CancellationToken.None);

        await client.FlushPendingAsync(CancellationToken.None);

        Assert.Equal(3, signer.Calls.Count);
        Assert.EndsWith("/audit", signer.Calls[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("/audit", signer.Calls[1].Path, StringComparison.Ordinal);
        Assert.EndsWith("/complete", signer.Calls[2].Path, StringComparison.Ordinal);
        using var completion = JsonDocument.Parse(signer.Calls[2].ExactJson);
        var root = completion.RootElement;
        Assert.Equal(2, root.GetProperty("auditEventCount").GetInt32());
        Assert.Equal(secondId.ToString("D"), root.GetProperty("finalEventId").GetString());
        var expectedChain = Sha256(
            signer.ReceiptDigests[firstId] + "\n" + signer.ReceiptDigests[secondId]);
        Assert.Equal(expectedChain, root.GetProperty("auditChainDigest").GetString());
        Assert.Equal(
            ["accepted"], db.GetWorkflowAuditAttemptOutcomes(firstId));
        Assert.Equal(
            ["accepted"], db.GetWorkflowAuditAttemptOutcomes(secondId));
        var intent = Assert.IsType<AgentStateDb.WorkflowCompletionIntentEntry>(
            db.GetWorkflowCompletionIntent(runId));
        Assert.Equal(
            ["accepted"],
            db.GetWorkflowCompletionAttemptOutcomes(intent.CompletionId));
        Assert.Equal(
            WorkflowAuditCloudClient.ComputeCompletionReceiptDigest(
                runId,
                AgentId,
                PharmacyId,
                intent.CompletionId,
                intent.AuditEventCount,
                intent.FinalEventId,
                expectedChain,
                intent.Outcome,
                intent.ReasonCode),
            db.GetAcceptedWorkflowCompletionReceiptDigest(intent.CompletionId));
    }

    [Fact]
    public async Task UnsignedResponse_IsRetryOnlyAndCompletionStaysBlocked()
    {
        using var db = OpenDb();
        var signer = new ReceiptSigner(AgentId, PharmacyId)
        {
            ResponseMode = ReceiptResponseMode.Unsigned,
        };
        var client = CreateClient(db, signer);
        var runId = Guid.NewGuid().ToString("D");
        var eventId = Guid.NewGuid();
        await client.PostStepAuditAsync(
            Event(eventId, runId, 0, "pioneerrx_query", "failed"),
            CancellationToken.None);
        await client.PostRunCompletedAsync(
            runId, WorkflowRunOutcome.Failed, "execution_failed",
            CancellationToken.None);

        await client.FlushPendingAsync(CancellationToken.None);

        Assert.Equal(["retry_unsigned"], db.GetWorkflowAuditAttemptOutcomes(eventId));
        Assert.Single(signer.Calls);
        Assert.EndsWith("/audit", signer.Calls[0].Path, StringComparison.Ordinal);
        var intent = Assert.IsType<AgentStateDb.WorkflowCompletionIntentEntry>(
            db.GetWorkflowCompletionIntent(runId));
        Assert.Empty(db.GetWorkflowCompletionAttemptOutcomes(intent.CompletionId));
    }

    [Theory]
    [InlineData(ReceiptResponseMode.CrossAgent)]
    [InlineData(ReceiptResponseMode.UnknownField)]
    [InlineData(ReceiptResponseMode.WrongEvent)]
    [InlineData(ReceiptResponseMode.BadAuditDigest)]
    public async Task SignedButUnboundReceipt_IsNeverAccepted(
        ReceiptResponseMode responseMode)
    {
        using var db = OpenDb();
        var signer = new ReceiptSigner(AgentId, PharmacyId)
        {
            ResponseMode = responseMode,
        };
        var client = CreateClient(db, signer);
        var runId = Guid.NewGuid().ToString("D");
        var eventId = Guid.NewGuid();
        await client.PostStepAuditAsync(
            Event(eventId, runId, 0, "pioneerrx_query", "success"),
            CancellationToken.None);

        await client.FlushPendingAsync(CancellationToken.None);

        Assert.Equal(
            ["retry_invalid_receipt"],
            db.GetWorkflowAuditAttemptOutcomes(eventId));
    }

    [Fact]
    public async Task SignedTerminalRejection_IsDurableAndNeverRetried()
    {
        using var db = OpenDb();
        var signer = new ReceiptSigner(AgentId, PharmacyId)
        {
            ResponseMode = ReceiptResponseMode.TerminalRejection,
        };
        var client = CreateClient(db, signer);
        var runId = Guid.NewGuid().ToString("D");
        var eventId = Guid.NewGuid();
        await client.PostStepAuditAsync(
            Event(eventId, runId, 0, "pioneerrx_query", "success"),
            CancellationToken.None);

        await client.FlushPendingAsync(CancellationToken.None);
        await client.FlushPendingAsync(CancellationToken.None);

        Assert.Single(signer.Calls);
        Assert.Equal(
            ["terminal_rejection"],
            db.GetWorkflowAuditAttemptOutcomes(eventId));
    }

    [Fact]
    public async Task SignedControlFlowCompletionRejection_IsTerminal()
    {
        using var db = OpenDb();
        var signer = new ReceiptSigner(AgentId, PharmacyId)
        {
            ResponseMode = ReceiptResponseMode.ControlFlowCompletionRejection,
        };
        var client = CreateClient(db, signer);
        var runId = Guid.NewGuid().ToString("D");
        await client.PostStepAuditAsync(
            Event(Guid.NewGuid(), runId, 0, "pioneerrx_query", "success"),
            CancellationToken.None);
        await client.PostRunCompletedAsync(
            runId, WorkflowRunOutcome.Completed, null, CancellationToken.None);

        await client.FlushPendingAsync(CancellationToken.None);
        await client.FlushPendingAsync(CancellationToken.None);

        Assert.Equal(2, signer.Calls.Count);
        var intent = Assert.IsType<AgentStateDb.WorkflowCompletionIntentEntry>(
            db.GetWorkflowCompletionIntent(runId));
        Assert.Equal(
            ["terminal_rejection"],
            db.GetWorkflowCompletionAttemptOutcomes(intent.CompletionId));
    }

    [Fact]
    public async Task Restart_DrainsStagedEvidenceWithoutReexecutingWork()
    {
        var path = Path.Combine(_directory, "restart.db");
        var runId = Guid.NewGuid().ToString("D");
        var eventId = Guid.NewGuid();
        Guid completionId;
        using (var firstDb = new AgentStateDb(path))
        {
            var firstClient = CreateClient(
                firstDb, new ReceiptSigner(AgentId, PharmacyId));
            await firstClient.PostStepAuditAsync(
                Event(eventId, runId, 0, "pioneerrx_query", "success"),
                CancellationToken.None);
            await firstClient.PostRunCompletedAsync(
                runId, WorkflowRunOutcome.Completed, null,
                CancellationToken.None);
            completionId = firstDb.GetWorkflowCompletionIntent(runId)!.CompletionId;
        }

        var signer = new ReceiptSigner(AgentId, PharmacyId);
        using var recoveredDb = new AgentStateDb(path);
        var recoveredClient = CreateClient(recoveredDb, signer);
        await recoveredClient.FlushPendingAsync(CancellationToken.None);

        Assert.Equal(2, signer.Calls.Count);
        Assert.Equal(
            ["accepted"], recoveredDb.GetWorkflowAuditAttemptOutcomes(eventId));
        Assert.Equal(
            ["accepted"],
            recoveredDb.GetWorkflowCompletionAttemptOutcomes(completionId));
    }

    [Fact]
    public async Task EventIdReplay_WithDifferentFactsFailsClosed()
    {
        using var db = OpenDb();
        var client = CreateClient(db, new ReceiptSigner(AgentId, PharmacyId));
        var runId = Guid.NewGuid().ToString("D");
        var eventId = Guid.NewGuid();
        await client.PostStepAuditAsync(
            Event(eventId, runId, 0, "pioneerrx_query", "success"),
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.PostStepAuditAsync(
                Event(eventId, runId, 1, "pioneerrx_query", "failed"),
                CancellationToken.None));
        Assert.Single(db.GetWorkflowAuditEvents(runId));
    }

    [Fact]
    public async Task CompletionReceiptDigestMismatch_IsNeverAcceptedOrPersisted()
    {
        using var db = OpenDb();
        var signer = new ReceiptSigner(AgentId, PharmacyId)
        {
            ResponseMode = ReceiptResponseMode.BadCompletionDigest,
        };
        var client = CreateClient(db, signer);
        var runId = Guid.NewGuid().ToString("D");
        await client.PostStepAuditAsync(
            Event(Guid.NewGuid(), runId, 0, "pioneerrx_query", "success"),
            CancellationToken.None);
        await client.PostRunCompletedAsync(
            runId, WorkflowRunOutcome.Completed, null, CancellationToken.None);

        await client.FlushPendingAsync(CancellationToken.None);

        var intent = db.GetWorkflowCompletionIntent(runId)!;
        Assert.Equal(
            ["retry_invalid_receipt"],
            db.GetWorkflowCompletionAttemptOutcomes(intent.CompletionId));
        Assert.Null(db.GetAcceptedWorkflowCompletionReceiptDigest(
            intent.CompletionId));
    }

    [Fact]
    public async Task RetryFailureThenSuccess_CanCompleteWithFullOrderedEvidence()
    {
        using var db = OpenDb();
        var signer = new ReceiptSigner(AgentId, PharmacyId);
        var client = CreateClient(db, signer);
        var runId = Guid.NewGuid().ToString("D");
        var failedId = Guid.NewGuid();
        var successId = Guid.NewGuid();
        await client.PostStepAuditAsync(
            Event(failedId, runId, 3, "pioneerrx_query", "failed"),
            CancellationToken.None);
        await client.PostStepAuditAsync(
            Event(successId, runId, 3, "pioneerrx_query", "success"),
            CancellationToken.None);

        await client.PostRunCompletedAsync(
            runId, WorkflowRunOutcome.Completed, null, CancellationToken.None);
        await client.FlushPendingAsync(CancellationToken.None);

        Assert.Equal(3, signer.Calls.Count);
        using var completion = JsonDocument.Parse(signer.Calls[2].ExactJson);
        Assert.Equal(2, completion.RootElement
            .GetProperty("auditEventCount").GetInt32());
        Assert.Equal(successId.ToString("D"), completion.RootElement
            .GetProperty("finalEventId").GetString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private AgentStateDb OpenDb() =>
        new(Path.Combine(_directory, $"{Guid.NewGuid():N}.db"));

    private static WorkflowAuditCloudClient CreateClient(
        AgentStateDb db,
        IPostSigner signer) =>
        new(
            signer,
            db,
            new AgentOptions { AgentId = AgentId, PharmacyId = PharmacyId },
            NullLogger<WorkflowAuditCloudClient>.Instance);

    private static WorkflowStepAuditEntry Event(
        Guid eventId,
        string runId,
        int stepIndex,
        string verb,
        string outcome) =>
        new(
            eventId,
            runId,
            stepIndex,
            verb,
            "1.0.0",
            RequestedDryRun: false,
            outcome,
            ExecDurationMs: outcome == "skipped" ? 0 : 12,
            ErrorKind: outcome == "success" ? null :
                outcome == "skipped" ? "condition_not_met" : "execution_failed",
            ParamsFieldCount: 2,
            BeforeStateFieldCount: null,
            AfterStateFieldCount: outcome == "success" ? 1 : null,
            EffectiveDryRun: null);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    public enum ReceiptResponseMode
    {
        Valid,
        Unsigned,
        CrossAgent,
        UnknownField,
        WrongEvent,
        BadAuditDigest,
        TerminalRejection,
        ControlFlowCompletionRejection,
        BadCompletionDigest,
    }

    private sealed class ReceiptSigner : IPostSigner
    {
        private static readonly string Signature =
            Convert.ToBase64String(new byte[64]);
        private readonly string _agentId;
        private readonly string _pharmacyId;

        internal ReceiptSigner(string agentId, string pharmacyId)
        {
            _agentId = agentId;
            _pharmacyId = pharmacyId;
        }

        internal ReceiptResponseMode ResponseMode { get; init; }
        internal List<(string Path, string ExactJson)> Calls { get; } = [];
        internal Dictionary<Guid, string> ReceiptDigests { get; } = [];

        public Task<JsonElement?> PostSignedAsync(
            string path, object payload, CancellationToken ct) =>
            Task.FromResult<JsonElement?>(null);

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) =>
            Task.FromResult<JsonElement?>(null);

        public Task<VerifiedCloudPostResponse?> PostSignedJsonResponseVerifiedAsync(
            string path,
            string exactJson,
            CancellationToken ct)
        {
            Calls.Add((path, exactJson));
            if (ResponseMode == ReceiptResponseMode.Unsigned)
                return Task.FromResult<VerifiedCloudPostResponse?>(null);
            using var request = JsonDocument.Parse(exactJson);
            var runId = path.Split('/', StringSplitOptions.RemoveEmptyEntries)[3];
            if (path.EndsWith("/audit", StringComparison.Ordinal))
                return Task.FromResult<VerifiedCloudPostResponse?>(
                    AuditResponse(request.RootElement, runId));
            return Task.FromResult<VerifiedCloudPostResponse?>(
                CompletionResponse(request.RootElement, runId));
        }

        private VerifiedCloudPostResponse AuditResponse(
            JsonElement request,
            string runId)
        {
            if (ResponseMode == ReceiptResponseMode.TerminalRejection)
                return Response(409, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    kind = "workflow_audit_rejection",
                    accepted = false,
                    terminal = true,
                    code = "workflow_run_terminal",
                }));
            var eventId = Guid.Parse(request.GetProperty("eventId").GetString()!);
            var receiptDigest = WorkflowAuditCloudClient.ComputeAuditReceiptDigest(
                runId,
                _agentId,
                _pharmacyId,
                eventId,
                request.GetProperty("executionOrdinal").GetInt32(),
                request.GetProperty("stepIndex").GetInt32(),
                request.GetProperty("verbName").GetString()!,
                request.GetProperty("verbVersion").GetString()!,
                request.GetProperty("requestedDryRun").GetBoolean(),
                NullableBool(request, "effectiveDryRun"),
                request.GetProperty("outcome").GetString()!,
                NullableInt(request, "execDurationMs"),
                NullableString(request, "errorKind"),
                request.GetProperty("paramsFieldCount").GetInt32(),
                NullableInt(request, "beforeStateFieldCount"),
                NullableInt(request, "afterStateFieldCount"));
            ReceiptDigests[eventId] = receiptDigest;
            var response = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["kind"] = "workflow_audit_receipt",
                ["workflowRunId"] = runId,
                ["agentInstanceId"] = ResponseMode == ReceiptResponseMode.CrossAgent
                    ? Guid.NewGuid().ToString("D") : _agentId,
                ["pharmacyId"] = _pharmacyId,
                ["eventId"] = ResponseMode == ReceiptResponseMode.WrongEvent
                    ? Guid.NewGuid().ToString("D") : eventId.ToString("D"),
                ["executionOrdinal"] =
                    request.GetProperty("executionOrdinal").GetInt32(),
                ["auditId"] = Guid.NewGuid().ToString("D"),
                ["receiptDigest"] = ResponseMode == ReceiptResponseMode.BadAuditDigest
                    ? new string('0', 64)
                    : receiptDigest,
                ["idempotent"] = false,
            };
            if (ResponseMode == ReceiptResponseMode.UnknownField)
                response["unexpected"] = true;
            return Response(200, JsonSerializer.Serialize(response));
        }

        private static bool? NullableBool(JsonElement root, string name) =>
            root.GetProperty(name).ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty(name).GetBoolean();

        private static int? NullableInt(JsonElement root, string name) =>
            root.GetProperty(name).ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty(name).GetInt32();

        private static string? NullableString(JsonElement root, string name) =>
            root.GetProperty(name).ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty(name).GetString();

        private VerifiedCloudPostResponse CompletionResponse(
            JsonElement request,
            string runId)
        {
            if (ResponseMode == ReceiptResponseMode.ControlFlowCompletionRejection)
                return Response(409, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    kind = "workflow_completion_rejection",
                    accepted = false,
                    terminal = true,
                    code = "workflow_completion_control_flow_mismatch",
                }));
            var outcome = request.GetProperty("outcome").GetString()!;
            var completionId = Guid.Parse(
                request.GetProperty("completionId").GetString()!);
            var finalEventId = request.GetProperty("finalEventId").ValueKind ==
                JsonValueKind.Null
                    ? (Guid?)null
                    : Guid.Parse(request.GetProperty("finalEventId").GetString()!);
            var auditEventCount =
                request.GetProperty("auditEventCount").GetInt32();
            var auditChainDigest =
                request.GetProperty("auditChainDigest").GetString()!;
            var reasonCode = request.GetProperty("reasonCode").ValueKind ==
                JsonValueKind.Null
                    ? null
                    : request.GetProperty("reasonCode").GetString();
            var completionReceiptDigest =
                WorkflowAuditCloudClient.ComputeCompletionReceiptDigest(
                    runId,
                    _agentId,
                    _pharmacyId,
                    completionId,
                    auditEventCount,
                    finalEventId,
                    auditChainDigest,
                    outcome,
                    reasonCode);
            var response = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                kind = "workflow_completion_receipt",
                workflowRunId = runId,
                agentInstanceId = _agentId,
                pharmacyId = _pharmacyId,
                completionId = completionId.ToString("D"),
                auditEventCount,
                finalEventId = finalEventId?.ToString("D"),
                auditChainDigest,
                completionReceiptDigest =
                    ResponseMode == ReceiptResponseMode.BadCompletionDigest
                        ? new string('0', 64)
                        : completionReceiptDigest,
                status = outcome,
                reasonCode,
                completedAt = outcome == "aborted" ? null :
                    DateTimeOffset.UtcNow.ToString("O"),
                abortedAt = outcome == "aborted" ?
                    DateTimeOffset.UtcNow.ToString("O") : null,
                idempotent = false,
            });
            return Response(200, response);
        }

        private static VerifiedCloudPostResponse Response(int status, string body) =>
            new(status, body, Sha256(body), "suavo-cmd-v1", Signature);
    }
}
