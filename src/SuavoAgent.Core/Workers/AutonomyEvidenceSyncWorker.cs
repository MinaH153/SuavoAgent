using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

internal sealed class AutonomyEvidenceSyncWorker : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly AgentStateDb _db;
    private readonly IPostSigner _cloud;
    private readonly ILogger<AutonomyEvidenceSyncWorker> _logger;
    private readonly TimeProvider _clock;

    internal AutonomyEvidenceSyncWorker(
        AgentStateDb db,
        IPostSigner cloud,
        ILogger<AutonomyEvidenceSyncWorker> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _cloud = cloud;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await SyncOnceAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(PollInterval, _clock, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var accepted = 0;
        foreach (var pending in _db.GetPendingAutonomyEvidence(10))
        {
            var signed = pending.Signed;
            try
            {
                var response = await _cloud.PostSignedAsync(
                    "/api/agent/autonomy-evidence",
                    new
                    {
                        receipt = signed.Receipt,
                        keyId = signed.KeyId,
                        signature = signed.Signature,
                        canonicalDigest = signed.CanonicalDigest,
                    },
                    ct).ConfigureAwait(false);
                if (!ExactAcceptance(response, signed.Receipt))
                {
                    _db.DelayAutonomyEvidence(signed.Receipt.ReceiptId, pending.AttemptCount);
                    break;
                }
                _db.MarkAutonomyEvidenceAccepted(
                    signed.Receipt.ReceiptId,
                    signed.Receipt.Counter,
                    signed.Receipt.ScopeDigest);
                accepted += 1;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogSafeWarning(ex);
                _db.DelayAutonomyEvidence(signed.Receipt.ReceiptId, pending.AttemptCount);
                // The cloud counter is sequential. Never skip a failed receipt
                // and submit a later counter out of order.
                break;
            }
        }
        return accepted;
    }

    private static bool ExactAcceptance(
        JsonElement? response,
        AutonomyEvidenceDeviceReceipt receipt)
    {
        if (response is null || response.Value.ValueKind != JsonValueKind.Object ||
            !response.Value.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !response.Value.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object)
            return false;
        return data.TryGetProperty("receiptId", out var receiptId) &&
            string.Equals(receiptId.GetString(), receipt.ReceiptId, StringComparison.Ordinal) &&
            data.TryGetProperty("scopeDigest", out var scopeDigest) &&
            string.Equals(scopeDigest.GetString(), receipt.ScopeDigest, StringComparison.Ordinal) &&
            data.TryGetProperty("counter", out var counter) &&
            counter.TryGetInt64(out var acceptedCounter) &&
            acceptedCounter == receipt.Counter;
    }
}
