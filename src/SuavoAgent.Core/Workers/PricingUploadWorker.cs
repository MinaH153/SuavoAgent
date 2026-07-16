using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Polls the exact HMAC upload channel and durably stages workbooks into the
/// opaque local intake. It never parses/logs workbook text and never ACKs a
/// fetch until the workbook and receipt have been flushed and atomically moved.
/// </summary>
internal sealed class PricingUploadWorker : ResilientHostedService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly IPricingUploadCloudClient _cloud;
    private readonly PricingUploadInbox _inbox;
    private readonly PricingJobCloudUploader _resultUploader;
    private readonly AgentStateDb _stateDb;
    private readonly ILogger<PricingUploadWorker> _logger;

    internal PricingUploadWorker(
        IPricingUploadCloudClient cloud,
        PricingUploadInbox inbox,
        PricingJobCloudUploader resultUploader,
        AgentStateDb stateDb,
        ILogger<PricingUploadWorker> logger,
        WorkerHealthRegistry? healthRegistry = null)
        : base(logger, healthRegistry)
    {
        _cloud = cloud;
        _inbox = inbox;
        _resultUploader = resultUploader;
        _stateDb = stateDb;
        _logger = logger;
    }

    protected override string WorkerName => "pricing-upload";

    protected override async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _inbox.ReconcileTemporaryFilesAsync(stoppingToken)
                    .ConfigureAwait(false);
                await _resultUploader.FlushPendingAsync(stoppingToken)
                    .ConfigureAwait(false);
                foreach (var accepted in
                         _stateDb.GetAcceptedPricingSourcesToFinalize(20))
                {
                    var sourceId = accepted.SourceUploadId ??
                        throw new InvalidOperationException(
                            "pricing_result_source_identity_missing");
                    await _inbox.CompleteAcceptedResultSyncAsync(
                            sourceId,
                            accepted.JobId,
                            accepted.PayloadSha256,
                            accepted.ExecutionOk,
                            stoppingToken)
                        .ConfigureAwait(false);
                    _stateDb.MarkPricingResultSourceFinalized(
                        sourceId, accepted.JobId, accepted.PayloadSha256);
                }
                await _inbox.FlushTerminalReceiptsAsync(_cloud, stoppingToken)
                    .ConfigureAwait(false);
                var uploads = await _cloud.PollAsync(stoppingToken).ConfigureAwait(false);
                foreach (var descriptor in uploads)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        await _inbox.StageAsync(_cloud, descriptor, stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (PricingWorkbookContentException)
                    {
                        await FailStructurallyAsync(
                            descriptor.Id, "native_validation_failed", stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (PricingUploadTransportException ex) when (IsIntegrityFailure(ex.Code))
                    {
                        await FailStructurallyAsync(
                            descriptor.Id, "integrity_mismatch", stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (InvalidDataException)
                    {
                        await FailStructurallyAsync(
                            descriptor.Id, "native_validation_failed", stoppingToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (PricingUploadTransportException ex)
            {
                _logger.LogWarning(
                    "Pricing upload channel unavailable code={Code}", ex.Code);
            }
            catch (HttpRequestException)
            {
                _logger.LogWarning("Pricing upload channel unavailable code=network_error");
            }
            catch (IOException)
            {
                _logger.LogWarning("Pricing upload intake unavailable code=local_io_error");
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task FailStructurallyAsync(
        Guid id,
        string reasonCode,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "Pricing upload rejected by local intake policy code={Code}", reasonCode);
        await _cloud.AckLifecycleAsync(id, false, reasonCode, ct).ConfigureAwait(false);
    }

    private static bool IsIntegrityFailure(string code) => code is
        "pricing_upload_content_headers_invalid" or
        "pricing_upload_content_oversize" or
        "pricing_upload_content_integrity_mismatch" or
        "pricing_upload_native_validation_mismatch";
}
