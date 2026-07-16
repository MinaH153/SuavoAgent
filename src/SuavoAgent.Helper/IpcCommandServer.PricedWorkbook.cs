using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper;

public sealed partial class IpcCommandServer
{
    private async Task<IpcResponse> HandlePioneerRxPricedWorkbookBeginAsync(
        IpcRequest request,
        CancellationToken ct)
    {
        if (!TryDeserializePricedWorkbookRequest(
                request,
                out PioneerRxPricedWorkbookBeginRequest? publicationRequest,
                out var error))
            return error!;
        var result = _pricedWorkbookStore is null
            ? PioneerRxPricedWorkbookBeginResult.Failed(
                publicationRequest!.JobId,
                PioneerRxPricedWorkbookPublicationCodes.DestinationUnavailable)
            : await _pricedWorkbookStore.BeginAsync(publicationRequest!, ct)
                .ConfigureAwait(false);
        return Ok(request.Id, request.Command, JsonSerializer.SerializeToElement(result));
    }

    private async Task<IpcResponse> HandlePioneerRxPricedWorkbookChunkAsync(
        IpcRequest request,
        CancellationToken ct)
    {
        if (!TryDeserializePricedWorkbookRequest(
                request,
                out PioneerRxPricedWorkbookChunkRequest? publicationRequest,
                out var error))
            return error!;
        var result = _pricedWorkbookStore is null
            ? PioneerRxPricedWorkbookChunkResult.Failed(
                publicationRequest!.JobId,
                PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable)
            : await _pricedWorkbookStore.AppendAsync(publicationRequest!, ct)
                .ConfigureAwait(false);
        return Ok(request.Id, request.Command, JsonSerializer.SerializeToElement(result));
    }

    private async Task<IpcResponse> HandlePioneerRxPricedWorkbookCommitAsync(
        IpcRequest request,
        CancellationToken ct)
    {
        if (!TryDeserializePricedWorkbookRequest(
                request,
                out PioneerRxPricedWorkbookCommitRequest? publicationRequest,
                out var error))
            return error!;
        var result = _pricedWorkbookStore is null
            ? PioneerRxPricedWorkbookCommitResult.Failed(
                publicationRequest!.JobId,
                PioneerRxPricedWorkbookPublicationCodes.DestinationUnavailable)
            : await _pricedWorkbookStore.CommitAsync(publicationRequest!, ct)
                .ConfigureAwait(false);
        return Ok(request.Id, request.Command, JsonSerializer.SerializeToElement(result));
    }

    private bool TryDeserializePricedWorkbookRequest<TRequest>(
        IpcRequest request,
        out TRequest? publicationRequest,
        out IpcResponse? error)
        where TRequest : class
    {
        publicationRequest = null;
        error = null;
        if (!_visionGenerationGate.IsMatched)
        {
            error = Error(
                request.Id,
                request.Command,
                "vision_generation_unconfirmed",
                "Workbook publication refused until Core and Helper prove the same local configuration generation.",
                IpcStatus.Forbidden);
            return false;
        }
        if (request.Version !=
                PioneerRxPricedWorkbookPublicationContract.CurrentVersion ||
            request.Data is null)
        {
            error = BadPublicationRequest(request);
            return false;
        }
        try
        {
            publicationRequest = JsonSerializer.Deserialize<TRequest>(request.Data.Value);
        }
        catch
        {
            error = BadPublicationRequest(request);
            return false;
        }
        var valid = publicationRequest switch
        {
            PioneerRxPricedWorkbookBeginRequest begin => begin.IsValid(),
            PioneerRxPricedWorkbookChunkRequest chunk => chunk.IsValid(),
            PioneerRxPricedWorkbookCommitRequest commit => commit.IsValid(),
            _ => false,
        };
        if (valid) return true;
        error = BadPublicationRequest(request);
        return false;
    }

    private static IpcResponse BadPublicationRequest(IpcRequest request) => Error(
        request.Id,
        request.Command,
        "bad_request",
        "The workbook publication request was invalid.",
        IpcStatus.BadRequest);
}
