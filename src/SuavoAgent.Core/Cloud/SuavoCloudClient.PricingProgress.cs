using System.Text.Json;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Cloud;

internal interface IPricingProgressTransport
{
    Task<bool> TryPostPricingProgressAsync(
        PioneerRxTop500ExportProgress progress,
        CancellationToken ct);
}

public sealed partial class SuavoCloudClient : IPricingProgressTransport
{
    private const int MaximumPricingProgressItems = 500;

    internal async Task<bool> TryPostPricingProgressAsync(
        PioneerRxTop500ExportProgress progress,
        CancellationToken ct)
    {
        if (!IsValidPricingProgress(progress)) return false;
        try
        {
            var response = await PostSignedAsync(
                $"/api/agent/commands/{progress.JobId}/pricing-progress",
                new
                {
                    sequence = progress.Sequence,
                    stage = progress.Stage,
                    processed = progress.Processed,
                    total = progress.Total,
                    needsReview = progress.NeedsReview,
                    occurredAt = progress.OccurredAt.ToUniversalTime(),
                },
                ct).ConfigureAwait(false);
            return response is not null &&
                   IsExactPricingProgressReceipt(response.Value, progress);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Serilog.Log.Warning(
                "core.pricing.progress_publish_failed stage={Stage} exception_type={ExceptionType}",
                progress.Stage,
                exception.GetType().Name);
            return false;
        }
    }

    Task<bool> IPricingProgressTransport.TryPostPricingProgressAsync(
        PioneerRxTop500ExportProgress progress,
        CancellationToken ct) => TryPostPricingProgressAsync(progress, ct);

    internal static bool IsValidPricingProgress(
        PioneerRxTop500ExportProgress? progress)
    {
        if (progress is null ||
            !Guid.TryParseExact(progress.JobId, "D", out var commandId) ||
            !string.Equals(
                progress.JobId,
                commandId.ToString("D"),
                StringComparison.Ordinal) ||
            progress.JobId[14] != '4' ||
            progress.JobId[19] is not ('8' or '9' or 'a' or 'b') ||
            progress.Sequence is < 1 or > 10_000 ||
            progress.Total is < 0 or > MaximumPricingProgressItems ||
            progress.Processed < 0 || progress.Processed > progress.Total ||
            progress.NeedsReview < 0 ||
            progress.NeedsReview > progress.Processed ||
            progress.OccurredAt == default)
            return false;

        return progress.Stage switch
        {
            "waiting_to_start" =>
                progress.Sequence == 1 && CountsAreZero(progress),
            PioneerRxTop500ExportStages.GeneratingReport =>
                progress.Sequence == 2 && CountsAreZero(progress),
            PioneerRxTop500ExportStages.ExportingReport =>
                progress.Sequence == 3 && CountsAreZero(progress),
            "pricing_items" => progress.Sequence >= 4,
            "creating_spreadsheet" or "verifying_results" =>
                progress.Sequence >= 4 && progress.Processed == progress.Total,
            _ => false,
        };
    }

    private static bool CountsAreZero(PioneerRxTop500ExportProgress progress) =>
        progress.Processed == 0 && progress.Total == 0 && progress.NeedsReview == 0;

    internal static bool IsExactPricingProgressReceipt(
        JsonElement response,
        PioneerRxTop500ExportProgress expected)
    {
        if (response.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(response, "success", "data") ||
            !response.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True ||
            !response.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !HasExactProperties(
                data,
                "commandId",
                "sequence",
                "stage",
                "idempotent") ||
            !TryReadString(data, "commandId", out var commandId) ||
            !data.TryGetProperty("sequence", out var sequence) ||
            sequence.ValueKind != JsonValueKind.Number ||
            !sequence.TryGetInt32(out var sequenceValue) ||
            !TryReadString(data, "stage", out var stage) ||
            !data.TryGetProperty("idempotent", out var idempotent) ||
            idempotent.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
            return false;
        return commandId == expected.JobId &&
               sequenceValue == expected.Sequence &&
               stage == expected.Stage;
    }
}
