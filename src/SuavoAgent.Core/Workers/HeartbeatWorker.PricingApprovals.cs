using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    private void ProcessPricingApprovalProposalReceipts(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("pricingApprovalProposalReceipts", out var value))
            return;
        if (!PricingApprovalResponseContract.TryParseProposalReceipts(
                value,
                out var receipts,
                out var code))
        {
            _logger.LogWarning(
                "Rejected pricing approval proposal receipts: {Code}",
                code);
            return;
        }

        var trustedKeys = RemoteCommandTrust.CreateProductionKeyRegistry();
        foreach (var receipt in receipts)
        {
            if (!_stateDb.TryRecordPricingApprovalProposalReceipt(
                    receipt,
                    DateTimeOffset.UtcNow,
                    trustedKeys,
                    out code))
            {
                _logger.LogWarning(
                    "Rejected pricing approval proposal receipt: {Code}",
                    code);
            }
        }
    }

    private async Task<bool> HandlePricingApprovalLedgerCommandAsync(
        JsonElement signedCommand,
        SignedCommand envelope,
        CancellationToken ct)
    {
        var data = signedCommand.TryGetProperty("data", out var nested)
            ? nested
            : default;
        if (data.ValueKind != JsonValueKind.Object)
            return false;

        AgentStateDb.PricingApprovalLedgerResult result;
        string? commandId;
        var isRevocation = false;
        if (envelope.Command == PricingApprovalContract.InstallCommandName)
        {
            if (!PricingApprovalCommandContract.TryParseInstall(
                    data,
                    out var command,
                    out var code) ||
                command is null)
                return await TryAckPricingApprovalAsync(
                        ReadCommandId(data),
                        succeeded: false,
                        result: null,
                        error: code,
                        ct)
                    .ConfigureAwait(false);
            commandId = command.CommandId;
            result = _stateDb.ApplyPricingApprovalGrant(
                envelope,
                data.GetRawText(),
                command.CommandId,
                command.Grant,
                DateTimeOffset.UtcNow,
                RemoteCommandTrust.CreateProductionKeyRegistry());
        }
        else if (envelope.Command == PricingApprovalContract.RevokeCommandName)
        {
            isRevocation = true;
            if (!PricingApprovalCommandContract.TryParseRevoke(
                    data,
                    out var command,
                    out var code) ||
                command is null)
                return await TryAckPricingApprovalAsync(
                        ReadCommandId(data),
                        succeeded: false,
                        result: null,
                        error: code,
                        ct)
                    .ConfigureAwait(false);
            commandId = command.CommandId;
            result = _stateDb.ApplyPricingApprovalRevocation(
                envelope,
                data.GetRawText(),
                command.CommandId,
                command.Revocation,
                DateTimeOffset.UtcNow,
                RemoteCommandTrust.CreateProductionKeyRegistry());
        }
        else
        {
            return false;
        }

        if (isRevocation && result.Succeeded)
        {
            // The pricing command itself is detached so heartbeat can receive
            // revocation. Signal every active pricing lease immediately after
            // the tombstone is durable and before attempting the cloud ACK.
            var cancellation = _autopilotRuns.CancelRuns(
                AutopilotRunKind.Pricing);
            _logger.LogInformation(
                "core.pricing.pic_revocation_cancelled_active_runs count={Count} failures={Failures}",
                cancellation.SignalledRunCount,
                cancellation.CancellationSignalFailureCount);
        }

        var receipt = result.Succeeded
            ? new
            {
                status = result.Code,
                approvalId = result.ApprovalId,
                policyDigest = result.PolicyDigest,
            }
            : null;
        return await TryAckPricingApprovalAsync(
                commandId,
                result.Succeeded,
                receipt,
                result.Succeeded ? null : result.Code,
                ct)
            .ConfigureAwait(false);
    }

    private async Task<bool> TryAckPricingApprovalAsync(
        string? commandId,
        bool succeeded,
        object? result,
        string? error,
        CancellationToken ct)
    {
        if (_cloudClient is null || !CanonicalUuid(commandId)) return false;
        return await _cloudClient.TryAckCommandAsync(
                commandId!,
                succeeded,
                result,
                error,
                ct)
            .ConfigureAwait(false);
    }

    private static string? ReadCommandId(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty("commandId", out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool CanonicalUuid(string? value) =>
        value is not null && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);
}
