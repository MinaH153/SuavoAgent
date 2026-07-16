using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Cloud;

internal sealed class PricingCommandProgressPublisher
{
    private readonly IPricingProgressTransport _transport;
    private readonly string _commandId;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _nextSequence = 1;
    private bool _blocked;

    internal PricingCommandProgressPublisher(
        IPricingProgressTransport transport,
        string commandId,
        TimeProvider? clock = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        if (!Guid.TryParseExact(commandId, "D", out var parsed) ||
            commandId != parsed.ToString("D") ||
            commandId[14] != '4' ||
            commandId[19] is not ('8' or '9' or 'a' or 'b'))
            throw new ArgumentException(
                "Pricing progress command identifier is invalid.",
                nameof(commandId));
        _commandId = commandId;
        _clock = clock ?? TimeProvider.System;
    }

    internal ValueTask<bool> PublishWaitingToStartAsync(CancellationToken ct) =>
        PublishNextAsync("waiting_to_start", 0, 0, 0, ct);

    internal async ValueTask<bool> PublishFixedAsync(
        PioneerRxTop500ExportProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(progress);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_blocked) return false;
            if (progress.JobId != _commandId) return Block();
            if (progress.Sequence < _nextSequence) return true;
            if (progress.Sequence != _nextSequence) return Block();
            return await PublishHeldAsync(progress, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal ValueTask<bool> PublishPricingAsync(
        PricingJobLocalProgress progress,
        CancellationToken ct) => PublishNextAsync(
            progress.Phase switch
            {
                PricingJobLocalPhase.PricingItems => "pricing_items",
                PricingJobLocalPhase.CreatingSpreadsheet => "creating_spreadsheet",
                PricingJobLocalPhase.VerifyingResults => "verifying_results",
                _ => throw new ArgumentOutOfRangeException(nameof(progress)),
            },
            progress.ProcessedItems,
            progress.TotalItems,
            progress.NeedsReviewItems,
            ct);

    private async ValueTask<bool> PublishNextAsync(
        string stage,
        int processed,
        int total,
        int needsReview,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_blocked) return false;
            return await PublishHeldAsync(
                new PioneerRxTop500ExportProgress(
                    _commandId,
                    _nextSequence,
                    stage,
                    processed,
                    total,
                    needsReview,
                    _clock.GetUtcNow()),
                ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<bool> PublishHeldAsync(
        PioneerRxTop500ExportProgress progress,
        CancellationToken ct)
    {
        if (!SuavoCloudClient.IsValidPricingProgress(progress)) return Block();
        if (!await _transport.TryPostPricingProgressAsync(progress, ct)
                .ConfigureAwait(false))
            return Block();
        _nextSequence++;
        return true;
    }

    private bool Block()
    {
        _blocked = true;
        return false;
    }
}
