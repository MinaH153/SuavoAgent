using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

public sealed partial class RxDetectionWorker
{
    private const int MaxLearnedRowsPerPoll = 5_000;

    internal async Task<bool> TryRunLearnedFallbackAsync(string reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        using var lease = _learnedAdapterRegistry?.TryAcquire(now);
        if (lease is null)
        {
            _learnedFallbackHealthy = false;
            return false;
        }

        try
        {
            var health = await lease.Adapter.CheckHealthAsync(ct);
            if (!health.IsHealthy)
            {
                _learnedFallbackHealthy = false;
                _learnedAdapterRegistry!.ReportUnhealthy(
                    lease.Binding, now, "health_check_failed");
                return false;
            }

            await RetryPendingBatchesAsync(ct);
            var learnedRows = new List<RxReadyForDelivery>();
            string? cursor = null;
            while (learnedRows.Count < MaxLearnedRowsPerPoll)
            {
                var page = await lease.Adapter.PullReadyAsync(cursor, ct);
                if (page.Count > LearnedPmsAdapter.DetectionPageSize)
                    throw new InvalidDataException("Approved learned adapter exceeded its bounded page contract.");
                learnedRows.AddRange(page);
                if (page.Count < LearnedPmsAdapter.DetectionPageSize) break;
                var nextCursor = page[^1].RxNumber;
                if (string.IsNullOrWhiteSpace(nextCursor) ||
                    string.Equals(nextCursor, cursor, StringComparison.Ordinal))
                    throw new InvalidDataException("Approved learned adapter did not advance its cursor.");
                cursor = nextCursor;
            }
            if (learnedRows.Count >= MaxLearnedRowsPerPoll)
                throw new InvalidDataException("Approved learned detection exceeded its per-poll safety cap.");
            var readyRxs = learnedRows
                .Where(row => !string.IsNullOrWhiteSpace(row.RxNumber))
                .GroupBy(row => (row.RxNumber, row.FillNumber))
                .Select(group => ToMetadata(group.First()))
                .ToArray();

            LastDetectedCount = readyRxs.Length;
            LastDetectionTime = DateTimeOffset.UtcNow;

            if (readyRxs.Length > 0)
            {
                var hmacSalt = RequireHmacSalt();
                PersistRxCorrelations(
                    readyRxs,
                    hmacSalt,
                    RxCorrelationSourceKinds.LearnedApproved,
                    lease.Binding.TemplateDigest);
                var json = SerializeRxBatch(
                    readyRxs,
                    hmacSalt,
                    pharmacyId: _options.PharmacyId,
                    agentInstallId: _options.AgentId,
                    sourcePms: "learned-approved",
                    schemaSignature: $"learned.template.{lease.Binding.TemplateDigest}",
                    evidenceSourceKind: RxCorrelationSourceKinds.LearnedApproved,
                    evidenceSourceBinding: lease.Binding.TemplateDigest);
                if (!await TrySyncPayloadToCloudAsync(json, ct))
                    _stateDb.InsertUnsyncedBatch(json);
            }

            _learnedAdapterRegistry!.ReportHealthy(lease.Binding, DateTimeOffset.UtcNow);
            _learnedFallbackHealthy = true;
            SetDetectionSource("learned-approved", reason);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _learnedFallbackHealthy = false;
            _learnedAdapterRegistry!.ReportUnhealthy(
                lease.Binding, DateTimeOffset.UtcNow, ex.GetType().Name);
            _logger.LogWarning(
                "Approved learned Rx fallback failed (session={SessionId}, errorType={ErrorType})",
                lease.Binding.SessionId, ex.GetType().Name);
            return false;
        }
    }

    private async Task DelayUnavailableDetectionAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (IsDetectionDegraded(now) && !_degradedLogged)
        {
            _degradedLogged = true;
            _logger.LogCritical(
                "Rx detection DARK for {Seconds}s ({Failures} consecutive SQL failures) — no approved healthy source is available",
                SqlDarkSeconds(now), _consecutiveSqlFailures);
        }
        var backoff = _sqlBackoff.NextDelay();
        _logger.LogDebug("No healthy Rx detection source; retrying in {Delay}s", backoff.TotalSeconds);
        await Task.Delay(backoff, ct);
    }

    private static RxMetadata ToMetadata(RxReadyForDelivery row) =>
        new(
            RxNumber: row.RxNumber,
            DrugName: row.DrugName,
            Ndc: row.Ndc,
            DateFilled: null,
            Quantity: row.Quantity,
            StatusGuid: Guid.TryParse(row.StatusText, out var statusGuid) ? statusGuid : Guid.Empty,
            DetectedAt: row.DetectedAt,
            FillNumber: row.FillNumber,
            DaysSupply: row.DaysSupply,
            DrugSchedule: row.DrugSchedule);

    private void SetDetectionSource(string source, string reason)
    {
        if (string.Equals(_activeDetectionSource, source, StringComparison.Ordinal)) return;
        var previous = _activeDetectionSource;
        _activeDetectionSource = source;
        _logger.LogWarning(
            "Rx detection source changed {Previous} -> {Current} (reason={Reason})",
            previous, source, reason);
        try
        {
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: _options.PharmacyId ?? "unbound",
                EventType: "rx_detection_source_changed",
                FromState: previous,
                ToState: source,
                Trigger: reason,
                Actor: "system",
                SourceComponent: "rx_detection_worker"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Rx detection source-change audit failed (errorType={ErrorType})",
                ex.GetType().Name);
        }
    }

    private static string DigestPrefix(string digest) =>
        digest.Length >= 12 ? digest[..12] : "invalid";
}
