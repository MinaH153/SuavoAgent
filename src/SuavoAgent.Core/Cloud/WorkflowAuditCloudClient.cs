using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Durable PHI-negative workflow receipt publisher. Executor calls commit only
/// fixed structural facts to SQLite; the hosted flush worker is the sole owner
/// of network retries and never invokes a workflow verb or actuation surface.
/// </summary>
public sealed partial class WorkflowAuditCloudClient : IWorkflowAuditClient
{
    private const int SchemaVersion = 1;
    private static readonly FrozenSet<string> VerbNames = new[]
    {
        "assert_element", "click_by_label", "click_by_signature",
        "launch_sandbox_app", "lookup_patient", "pioneerrx_click",
        "pioneerrx_query", "pioneerrx_writeback_rx_delivery", "press_keys",
        "query_top_ndcs_for_patient", "type_into_field",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ActuatingVerbs = new[]
    {
        "click_by_label", "click_by_signature", "launch_sandbox_app",
        "pioneerrx_click", "pioneerrx_writeback_rx_delivery", "press_keys",
        "type_into_field",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> Outcomes =
        new[] { "success", "rejected", "failed", "skipped" }
            .ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ErrorKinds = new[]
    {
        "authz_denied", "condition_not_met", "execution_exception",
        "execution_failed", "execution_timeout", "manifest_resolution_failed",
        "parameter_validation_failed", "postcondition_exception",
        "postcondition_failed", "precondition_exception",
        "precondition_failed", "rollback_capture_exception",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> CompletionReasonCodes = new[]
    {
        "authz_denied", "condition_not_met", "cooperative_cancel",
        "cycle_limit_exceeded", "execution_exception", "execution_failed",
        "execution_timeout", "gate_disabled", "gate_paused",
        "goto_target_unresolved", "kill_switch_tripped",
        "manifest_resolution_failed", "no_steps_in_definition",
        "parameter_validation_failed", "postcondition_exception",
        "postcondition_failed", "precondition_exception", "precondition_failed",
        "retry_exhausted", "rollback_capture_exception",
        "unknown_control_flow",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly Regex MachineVersion = new(
        @"^(?:[?]|[A-Za-z0-9][A-Za-z0-9._+-]{0,59})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IPostSigner _postSigner;
    private readonly AgentStateDb _db;
    private readonly ILogger<WorkflowAuditCloudClient> _logger;
    private readonly string _expectedAgentId;
    private readonly string _expectedPharmacyId;
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public WorkflowAuditCloudClient(
        IPostSigner postSigner,
        AgentStateDb db,
        AgentOptions options,
        ILogger<WorkflowAuditCloudClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _postSigner = postSigner ?? throw new ArgumentNullException(nameof(postSigner));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _expectedAgentId = NormalizeRequiredUuid(options.AgentId, "agent");
        _expectedPharmacyId = NormalizeRequiredUuid(options.PharmacyId, "pharmacy");
    }

    public Task PostStepAuditAsync(WorkflowStepAuditEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var runId = NormalizeRequiredUuid(entry.WorkflowRunId, "workflow run");
        ValidateEvent(entry);
        _db.StageWorkflowAuditEvent(
            entry.EventId,
            runId,
            entry.StepIndex,
            entry.VerbName,
            entry.VerbVersion,
            entry.RequestedDryRun,
            entry.EffectiveDryRun,
            entry.Outcome,
            entry.ExecDurationMs is null ? null : checked((int)entry.ExecDurationMs),
            entry.ErrorKind,
            entry.ParamsFieldCount,
            entry.BeforeStateFieldCount,
            entry.AfterStateFieldCount,
            ordinal => SerializeAuditPayload(entry, ordinal));
        return Task.CompletedTask;
    }

    public Task PostRunCompletedAsync(
        string workflowRunId,
        WorkflowRunOutcome outcome,
        string? abortReason,
        CancellationToken ct)
    {
        var runId = NormalizeRequiredUuid(workflowRunId, "workflow run");
        var outcomeCode = outcome switch
        {
            WorkflowRunOutcome.Completed => "completed",
            WorkflowRunOutcome.Failed => "failed",
            WorkflowRunOutcome.Aborted => "aborted",
            _ => throw new InvalidDataException("Workflow completion outcome is invalid."),
        };
        var reasonCode = MapCompletionReason(outcome, abortReason);
        _db.StageWorkflowCompletionIntent(
            Guid.NewGuid(), runId, outcomeCode, reasonCode);
        return Task.CompletedTask;
    }

    internal async Task FlushPendingAsync(CancellationToken ct)
    {
        await _flushGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < 100; i++)
            {
                ct.ThrowIfCancellationRequested();
                var entry = _db.GetNextDueWorkflowAuditEvent(DateTimeOffset.UtcNow);
                if (entry is null) break;
                await SendAuditEventAsync(entry, ct).ConfigureAwait(false);
            }

            for (var i = 0; i < 100; i++)
            {
                var materialization =
                    _db.GetNextWorkflowCompletionToMaterialize();
                if (materialization is null) break;
                MaterializeCompletion(materialization);
            }

            for (var i = 0; i < 100; i++)
            {
                ct.ThrowIfCancellationRequested();
                var completion =
                    _db.GetNextDueWorkflowCompletion(DateTimeOffset.UtcNow);
                if (completion is null) break;
                await SendCompletionAsync(completion, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private AgentStateDb.WorkflowAuditPayloadBytes SerializeAuditPayload(
        WorkflowStepAuditEntry entry,
        int executionOrdinal)
    {
        var payload = new WorkflowAuditRequest(
            SchemaVersion,
            entry.EventId.ToString("D"),
            executionOrdinal,
            entry.StepIndex,
            entry.VerbName,
            entry.VerbVersion,
            entry.RequestedDryRun,
            entry.EffectiveDryRun,
            entry.Outcome,
            entry.ExecDurationMs is null ? null : checked((int)entry.ExecDurationMs),
            entry.ErrorKind,
            entry.ParamsFieldCount,
            entry.BeforeStateFieldCount,
            entry.AfterStateFieldCount);
        var json = JsonSerializer.Serialize(payload);
        return new AgentStateDb.WorkflowAuditPayloadBytes(json, Sha256(json));
    }

    private void MaterializeCompletion(
        AgentStateDb.WorkflowCompletionMaterialization materialization)
    {
        var chainInput = string.Join('\n', materialization.AcceptedReceiptDigests);
        var chainDigest = Sha256Ascii(chainInput);
        var intent = materialization.Intent;
        var payload = new WorkflowCompletionRequest(
            SchemaVersion,
            intent.CompletionId.ToString("D"),
            intent.Outcome,
            intent.ReasonCode,
            intent.AuditEventCount,
            intent.FinalEventId?.ToString("D"),
            chainDigest);
        var json = JsonSerializer.Serialize(payload);
        _db.StageWorkflowCompletionPayload(
            intent, chainDigest, json, Sha256(json));
    }

    private static void ValidateEvent(WorkflowStepAuditEntry entry)
    {
        if (!IsUuidV4(entry.EventId) ||
            entry.StepIndex is < 0 or > 1024 ||
            !VerbNames.Contains(entry.VerbName) ||
            !MachineVersion.IsMatch(entry.VerbVersion) ||
            !Outcomes.Contains(entry.Outcome) ||
            entry.ExecDurationMs is < 0 or > 600_000 ||
            entry.ErrorKind is not null && !ErrorKinds.Contains(entry.ErrorKind) ||
            entry.ParamsFieldCount is < 0 or > 64 ||
            entry.BeforeStateFieldCount is < 0 or > 64 ||
            entry.AfterStateFieldCount is < 0 or > 64 ||
            entry.Outcome == "success" && entry.ErrorKind is not null ||
            entry.Outcome != "success" && entry.ErrorKind is null ||
            entry.RequestedDryRun && entry.EffectiveDryRun == false ||
            ActuatingVerbs.Contains(entry.VerbName) &&
                entry.EffectiveDryRun is null)
            throw new InvalidDataException(
                "Workflow audit event violates the PHI-negative wire contract.");
    }

    internal static string? MapCompletionReason(
        WorkflowRunOutcome outcome,
        string? rawReason)
    {
        if (outcome == WorkflowRunOutcome.Completed) return null;
        if (!string.IsNullOrEmpty(rawReason))
        {
            if (CompletionReasonCodes.Contains(rawReason)) return rawReason;
            var separator = rawReason.IndexOf(':');
            var prefix = (separator > 0 ? rawReason[..separator] : rawReason).Trim();
            if (CompletionReasonCodes.Contains(prefix)) return prefix;
            if (rawReason.StartsWith("gate_paused_until:", StringComparison.Ordinal))
                return "gate_paused";
            if (rawReason.StartsWith("unknown_control_flow", StringComparison.Ordinal))
                return "unknown_control_flow";
        }
        return "execution_failed";
    }

    private static string NormalizeRequiredUuid(string? value, string field)
    {
        if (!Guid.TryParseExact(value, "D", out var parsed))
            throw new InvalidDataException(
                $"Workflow audit {field} identity is invalid.");
        return parsed.ToString("D");
    }

    private static bool IsUuidV4(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '4' && text[19] is '8' or '9' or 'a' or 'b';
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Sha256Ascii(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)))
            .ToLowerInvariant();

    private sealed record WorkflowAuditRequest(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("eventId")] string EventId,
        [property: JsonPropertyName("executionOrdinal")] int ExecutionOrdinal,
        [property: JsonPropertyName("stepIndex")] int StepIndex,
        [property: JsonPropertyName("verbName")] string VerbName,
        [property: JsonPropertyName("verbVersion")] string VerbVersion,
        [property: JsonPropertyName("requestedDryRun")] bool RequestedDryRun,
        [property: JsonPropertyName("effectiveDryRun")] bool? EffectiveDryRun,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("execDurationMs")] int? ExecDurationMs,
        [property: JsonPropertyName("errorKind")] string? ErrorKind,
        [property: JsonPropertyName("paramsFieldCount")] int ParamsFieldCount,
        [property: JsonPropertyName("beforeStateFieldCount")] int? BeforeStateFieldCount,
        [property: JsonPropertyName("afterStateFieldCount")] int? AfterStateFieldCount);

    private sealed record WorkflowCompletionRequest(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("completionId")] string CompletionId,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("reasonCode")] string? ReasonCode,
        [property: JsonPropertyName("auditEventCount")] int AuditEventCount,
        [property: JsonPropertyName("finalEventId")] string? FinalEventId,
        [property: JsonPropertyName("auditChainDigest")] string AuditChainDigest);
}
