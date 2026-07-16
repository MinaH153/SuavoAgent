using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper;

public sealed partial class IpcCommandServer
{
    private async Task<IpcResponse> HandlePioneerRxTop500ExportAsync(
        IpcRequest request,
        CancellationToken ct)
    {
        if (!_visionGenerationGate.IsMatched)
        {
            return Error(
                request.Id,
                request.Command,
                "vision_generation_unconfirmed",
                "Report preparation refused until Core and Helper prove the same local configuration generation.",
                IpcStatus.Forbidden);
        }
        if (_top500Export is null)
        {
            return Error(
                request.Id,
                request.Command,
                "top500_export_unavailable",
                "Top-500 report preparation is unavailable on this agent.");
        }
        if (request.Data is null)
        {
            return Error(
                request.Id,
                request.Command,
                "bad_request",
                "Missing report request.",
                IpcStatus.BadRequest);
        }
        if (request.Version != PioneerRxTop500ExportRequest.CurrentContractVersion)
        {
            return Error(
                request.Id,
                request.Command,
                "bad_request",
                "The report request version was invalid.",
                IpcStatus.BadRequest);
        }

        PioneerRxTop500ExportRequest? exportRequest;
        try
        {
            exportRequest = JsonSerializer.Deserialize<PioneerRxTop500ExportRequest>(
                request.Data.Value);
        }
        catch (Exception)
        {
            return Error(
                request.Id,
                request.Command,
                "bad_request",
                "The report request was invalid.",
                IpcStatus.BadRequest);
        }
        if (exportRequest is null || !exportRequest.IsValid())
        {
            return Error(
                request.Id,
                request.Command,
                "bad_request",
                "The report request was invalid.",
                IpcStatus.BadRequest);
        }

        var result = await _top500Export.RunAsync(exportRequest, ct).ConfigureAwait(false);
        return Ok(
            request.Id,
            request.Command,
            JsonSerializer.SerializeToElement(result));
    }

    private async Task<IpcResponse> HandlePioneerRxTop500ReadArtifactAsync(
        IpcRequest request,
        CancellationToken ct)
    {
        if (!_visionGenerationGate.IsMatched)
        {
            return Error(
                request.Id,
                request.Command,
                "vision_generation_unconfirmed",
                "Artifact retrieval refused until Core and Helper prove the same local configuration generation.",
                IpcStatus.Forbidden);
        }
        if (_top500Export is null)
        {
            return Error(
                request.Id,
                request.Command,
                "top500_export_unavailable",
                "Top-500 artifact retrieval is unavailable on this agent.");
        }
        if (request.Version != PioneerRxTop500ArtifactReadRequest.CurrentContractVersion ||
            request.Data is null)
        {
            return Error(
                request.Id,
                request.Command,
                "bad_request",
                "The artifact request was invalid.",
                IpcStatus.BadRequest);
        }

        PioneerRxTop500ArtifactReadRequest? artifactRequest;
        try
        {
            artifactRequest = JsonSerializer.Deserialize<PioneerRxTop500ArtifactReadRequest>(
                request.Data.Value);
        }
        catch (Exception)
        {
            return Error(
                request.Id,
                request.Command,
                "bad_request",
                "The artifact request was invalid.",
                IpcStatus.BadRequest);
        }
        if (artifactRequest is null || !artifactRequest.IsValid())
        {
            return Error(
                request.Id,
                request.Command,
                "bad_request",
                "The artifact request was invalid.",
                IpcStatus.BadRequest);
        }

        var result = await _top500Export.ReadArtifactAsync(artifactRequest, ct)
            .ConfigureAwait(false);
        return Ok(
            request.Id,
            request.Command,
            JsonSerializer.SerializeToElement(result));
    }
}
