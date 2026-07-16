using System.Collections.Concurrent;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

public sealed class PioneerRxTop500ProgressRelay
{
    private sealed record Subscription(
        Func<PioneerRxTop500ExportProgress, CancellationToken, ValueTask> Callback);

    private sealed class Lease(
        PioneerRxTop500ProgressRelay owner,
        string jobId,
        Subscription subscription) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Remove(jobId, subscription);
        }
    }

    private readonly ConcurrentDictionary<string, Subscription> _subscriptions =
        new(StringComparer.Ordinal);

    internal IDisposable? TryRegister(
        string jobId,
        Func<PioneerRxTop500ExportProgress, CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!PioneerRxPricedWorkbookPublicationContract.IsCanonicalJobId(jobId))
            return null;
        var subscription = new Subscription(callback);
        return _subscriptions.TryAdd(jobId, subscription)
            ? new Lease(this, jobId, subscription)
            : null;
    }

    internal async Task<bool> TryReportAsync(
        PioneerRxTop500ExportProgress progress,
        CancellationToken ct)
    {
        if (progress is null ||
            !PioneerRxPricedWorkbookPublicationContract.IsCanonicalJobId(progress.JobId) ||
            progress.Stage switch
            {
                PioneerRxTop500ExportStages.GeneratingReport =>
                    progress.Sequence !=
                        PioneerRxTop500ExportStages.GeneratingReportSequence,
                PioneerRxTop500ExportStages.ExportingReport =>
                    progress.Sequence !=
                        PioneerRxTop500ExportStages.ExportingReportSequence,
                _ => true,
            } ||
            progress.Processed != 0 ||
            progress.Total != 0 ||
            progress.NeedsReview != 0 ||
            progress.OccurredAt == default ||
            !_subscriptions.TryGetValue(progress.JobId, out var subscription))
            return false;
        try
        {
            await subscription.Callback(progress, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void Remove(string jobId, Subscription subscription)
    {
        if (_subscriptions.TryGetValue(jobId, out var current) &&
            ReferenceEquals(current, subscription))
            _subscriptions.TryRemove(jobId, out _);
    }
}

internal static class PioneerRxTop500ProgressIpcProcessor
{
    internal static async Task<IpcResponse> ProcessAsync(
        IpcRequest request,
        PioneerRxTop500ProgressRelay relay,
        CancellationToken ct = default)
    {
        PioneerRxTop500ExportProgress? progress = null;
        try
        {
            if (request.Version == 1 && request.Data is not null)
                progress = JsonSerializer.Deserialize<PioneerRxTop500ExportProgress>(
                    request.Data.Value);
        }
        catch (JsonException)
        {
            // Rejected below without echoing local content.
        }
        var accepted = progress is not null &&
                       await relay.TryReportAsync(progress, ct).ConfigureAwait(false);
        return new IpcResponse(
            request.Id,
            accepted ? IpcStatus.Ok : IpcStatus.BadRequest,
            request.Command,
            progress is null
                ? null
                : JsonSerializer.SerializeToElement(
                    new PioneerRxTop500ProgressReceipt(
                        progress.JobId,
                        progress.Sequence,
                        accepted)),
            accepted
                ? null
                : new IpcError(
                    "pricing_progress_rejected",
                    "The local pricing progress update was rejected.",
                    false,
                    0));
    }
}
