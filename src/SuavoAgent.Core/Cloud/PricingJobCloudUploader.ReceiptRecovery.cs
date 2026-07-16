using System.Text.Json;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Cloud;

public sealed partial class PricingJobCloudUploader
{
    private const string RecoveryUnavailableCode =
        "pricing_result_receipt_recovery_unavailable";
    private const string ManualReconciliationCode =
        "pricing_result_manual_reconciliation_required";

    private async Task<PricingJobCloudUploadReceipt>
        SendOrRecoverPricingResultAsync(
            AgentStateDb.PricingResultOutboxEntry captured,
            JsonElement payload,
            string approvalId,
            string grantDigest,
            AgentStateDb.PricingAuthorityOperationContext context,
            CancellationToken ct)
    {
        // The authority gate is held here. Re-read the immutable evidence so a
        // second worker that captured the same pending row before waiting can
        // observe the first worker's durable terminal state without another
        // network transmission.
        var current = _db.GetPricingResultOutbox(captured.JobId);
        if (current is null || current.PayloadSha256 != captured.PayloadSha256)
            return new(false, "pricing_result_upload_failed", 0);

        var terminal = _db.GetPricingResultOutboxQuarantine(
            current.JobId,
            current.PayloadSha256);
        if (terminal is not null)
            return new(
                false,
                terminal.ReasonCode,
                0,
                terminal.HttpStatus is not null ||
                IsPermanentPricingAuthorityFailure(terminal.ReasonCode));

        if (current.State == "accepted")
        {
            if (current.AcceptedReceiptJson is null ||
                !TryParseSuccessReceipt(
                    current.AcceptedReceiptJson,
                    current,
                    _expectedAgentInstanceId,
                    _expectedPharmacyId,
                    requireIdempotent: false,
                    out var recorded))
                return new(false, "pricing_result_upload_receipt_invalid", 0);
            return new(true, "pricing_result_upload_accepted", recorded);
        }
        if (current.State != "pending")
            return new(false, "pricing_result_upload_failed", 0);

        return context.IsReconciliation
            ? await RecoverAndCommitPricingResultAsync(
                    current,
                    approvalId,
                    grantDigest,
                    context.RecoveryAttempt,
                    ct)
                .ConfigureAwait(false)
            : await SendAndCommitPricingResultAsync(current, payload, ct)
                .ConfigureAwait(false);
    }

    private async Task<PricingJobCloudUploadReceipt>
        RecoverAndCommitPricingResultAsync(
            AgentStateDb.PricingResultOutboxEntry entry,
            string approvalId,
            string grantDigest,
            int recoveryAttempt,
            CancellationToken ct)
    {
        if (entry.CommandId is null)
            return RecoveryUnavailable(entry, recoveryAttempt);

        VerifiedCloudPostResponse? verified;
        try
        {
            verified = await _postSigner.PostSignedResponseVerifiedAsync(
                $"/api/agent/pricing-jobs/{entry.JobId}/results/receipt-recovery",
                new
                {
                    commandId = entry.CommandId,
                    approvalId,
                    grantDigest,
                    payloadSha256 = entry.PayloadSha256,
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            _logger.LogSafeWarning(exception);
            return RecoveryUnavailable(entry, recoveryAttempt);
        }

        if (verified is null || !HasValidVerifiedEnvelope(verified))
            return RecoveryUnavailable(entry, recoveryAttempt);

        if (verified.StatusCode is < 200 or > 299)
        {
            if (!TryParseTerminalRejection(
                    verified.StatusCode,
                    verified.Body,
                    out var code,
                    out var exactResponse))
                return RecoveryUnavailable(entry, recoveryAttempt);
            return Quarantine(
                entry,
                code,
                verified.StatusCode,
                exactResponse,
                verified.KeyId,
                verified.SignatureBase64,
                verifiedTerminal: true);
        }

        if (!TryParseSuccessReceipt(
                verified.Body,
                entry,
                _expectedAgentInstanceId,
                _expectedPharmacyId,
                requireIdempotent: true,
                out var recordedCount))
            return RecoveryUnavailable(entry, recoveryAttempt);

        _db.MarkPricingResultPayloadAccepted(
            entry.JobId,
            entry.PayloadSha256,
            recordedCount,
            "pricing_result_upload_accepted",
            verified.Body,
            verified.KeyId,
            verified.SignatureBase64);
        return new(true, "pricing_result_upload_accepted", recordedCount);
    }

    private PricingJobCloudUploadReceipt RecoveryUnavailable(
        AgentStateDb.PricingResultOutboxEntry entry,
        int recoveryAttempt) => recoveryAttempt >= 3
            ? Quarantine(
                entry,
                ManualReconciliationCode,
                verifiedTerminal: true)
            : new(false, RecoveryUnavailableCode, 0);
}
