using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Cloud;

public sealed partial class WorkflowAuditCloudClient
{
    private static readonly FrozenDictionary<int, FrozenSet<string>>
        AuditTerminalCodes = new Dictionary<int, FrozenSet<string>>
        {
            [400] = Set("workflow_audit_invalid"),
            [403] = Set("workflow_tenant_mismatch"),
            [404] = Set("workflow_run_not_found"),
            [409] = Set(
                "workflow_definition_invalid", "workflow_step_mismatch",
                "workflow_audit_ordinal_gap",
                "workflow_audit_idempotency_conflict", "workflow_run_terminal"),
        }.ToFrozenDictionary();
    private static readonly FrozenDictionary<int, FrozenSet<string>>
        CompletionTerminalCodes = new Dictionary<int, FrozenSet<string>>
        {
            [400] = Set("workflow_completion_invalid"),
            [403] = Set("workflow_tenant_mismatch"),
            [404] = Set("workflow_run_not_found"),
            [409] = Set(
                "workflow_run_not_started", "workflow_completion_without_audit",
                "workflow_audit_incomplete",
                "workflow_completion_event_count_mismatch",
                "workflow_completion_digest_mismatch",
                "workflow_completion_control_flow_mismatch",
                "workflow_completion_idempotency_conflict",
                "workflow_run_wrong_state", "workflow_legacy_terminal_unverified"),
        }.ToFrozenDictionary();

    private async Task SendAuditEventAsync(
        AgentStateDb.WorkflowAuditEventOutboxEntry entry,
        CancellationToken ct)
    {
        if (!ValidatePersistedAuditPayload(entry))
            throw new InvalidDataException(
                "Durable workflow audit payload failed exact validation.");
        var now = DateTimeOffset.UtcNow;
        VerifiedCloudPostResponse? response;
        try
        {
            response = await _postSigner.PostSignedJsonResponseVerifiedAsync(
                    $"/api/agent/workflows/{entry.WorkflowRunId}/audit",
                    entry.PayloadJson,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            RecordAuditRetry(entry, "retry_transport", null, now);
            return;
        }
        if (response is null)
        {
            RecordAuditRetry(entry, "retry_unsigned", null, now);
            return;
        }
        if (!HasValidVerifiedEnvelope(response))
        {
            RecordAuditRetry(entry, "retry_invalid_receipt", response.StatusCode, now);
            return;
        }
        if (TryParseAuditReceipt(response, entry, out var receiptDigest))
        {
            _db.RecordWorkflowAuditSignedAttempt(
                entry.EventId, "accepted", response.StatusCode, now, null,
                receiptDigest, null, response.Body, response.BodySha256,
                response.KeyId, response.SignatureBase64);
            return;
        }
        if (TryParseRejection(
                response,
                "workflow_audit_rejection",
                AuditTerminalCodes,
                "workflow_audit_unavailable",
                out var terminal,
                out var code))
        {
            _db.RecordWorkflowAuditSignedAttempt(
                entry.EventId,
                terminal ? "terminal_rejection" : "retry_server_unavailable",
                response.StatusCode,
                now,
                terminal ? null : RetryAt(now, entry.AttemptCount),
                null,
                code,
                response.Body,
                response.BodySha256,
                response.KeyId,
                response.SignatureBase64);
            return;
        }
        RecordAuditRetry(entry, "retry_invalid_receipt", response.StatusCode, now);
    }

    private async Task SendCompletionAsync(
        AgentStateDb.WorkflowCompletionOutboxEntry entry,
        CancellationToken ct)
    {
        if (!ValidatePersistedCompletionPayload(entry))
            throw new InvalidDataException(
                "Durable workflow completion payload failed exact validation.");
        var now = DateTimeOffset.UtcNow;
        VerifiedCloudPostResponse? response;
        try
        {
            response = await _postSigner.PostSignedJsonResponseVerifiedAsync(
                    $"/api/agent/workflows/{entry.WorkflowRunId}/complete",
                    entry.PayloadJson,
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            RecordCompletionRetry(entry, "retry_transport", null, now);
            return;
        }
        if (response is null)
        {
            RecordCompletionRetry(entry, "retry_unsigned", null, now);
            return;
        }
        if (!HasValidVerifiedEnvelope(response))
        {
            RecordCompletionRetry(
                entry, "retry_invalid_receipt", response.StatusCode, now);
            return;
        }
        if (TryParseCompletionReceipt(
                response, entry, out var completionReceiptDigest))
        {
            _db.RecordWorkflowCompletionSignedAttempt(
                entry.CompletionId, "accepted", response.StatusCode, now,
                null, completionReceiptDigest, null, response.Body,
                response.BodySha256, response.KeyId, response.SignatureBase64);
            return;
        }
        if (TryParseRejection(
                response,
                "workflow_completion_rejection",
                CompletionTerminalCodes,
                "workflow_completion_unavailable",
                out var terminal,
                out var code))
        {
            _db.RecordWorkflowCompletionSignedAttempt(
                entry.CompletionId,
                terminal ? "terminal_rejection" : "retry_server_unavailable",
                response.StatusCode,
                now,
                terminal ? null : RetryAt(now, entry.AttemptCount),
                null,
                code,
                response.Body,
                response.BodySha256,
                response.KeyId,
                response.SignatureBase64);
            return;
        }
        RecordCompletionRetry(
            entry, "retry_invalid_receipt", response.StatusCode, now);
    }

    private void RecordAuditRetry(
        AgentStateDb.WorkflowAuditEventOutboxEntry entry,
        string code,
        int? status,
        DateTimeOffset now) =>
        _db.RecordWorkflowAuditRetry(
            entry.EventId, code, status, now, RetryAt(now, entry.AttemptCount));

    private void RecordCompletionRetry(
        AgentStateDb.WorkflowCompletionOutboxEntry entry,
        string code,
        int? status,
        DateTimeOffset now) =>
        _db.RecordWorkflowCompletionRetry(
            entry.CompletionId, code, status, now,
            RetryAt(now, entry.AttemptCount));

    private static DateTimeOffset RetryAt(DateTimeOffset now, int attemptCount)
    {
        var seconds = attemptCount switch
        {
            <= 0 => 5,
            1 => 15,
            2 => 60,
            3 => 300,
            _ => 900,
        };
        return now.AddSeconds(seconds);
    }

    private static FrozenSet<string> Set(params string[] values) =>
        values.ToFrozenSet(StringComparer.Ordinal);

    private static bool HasValidVerifiedEnvelope(VerifiedCloudPostResponse response)
    {
        if (response.StatusCode is < 100 or > 599 ||
            response.Body.Length == 0 ||
            Encoding.UTF8.GetByteCount(response.Body) > 16 * 1024 ||
            response.KeyId != RemoteCommandTrust.CommandV1KeyId)
            return false;
        try
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(response.Body));
            try
            {
                return CryptographicOperations.FixedTimeEquals(
                        digest, Convert.FromHexString(response.BodySha256)) &&
                    Convert.FromBase64String(response.SignatureBase64).Length == 64;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
