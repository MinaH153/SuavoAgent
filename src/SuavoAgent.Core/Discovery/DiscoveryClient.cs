using System.Text.Json;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Ipc;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Core-side wrapper around the <see cref="IpcCommandClient"/> for the
/// <c>find_file</c> IPC command. Helper runs the actual locator in the
/// interactive user session; this client just serializes the spec and
/// deserializes the result.
/// </summary>
public sealed class DiscoveryClient
{
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<DiscoveryClient> _logger;

    public DiscoveryClient(ILogger<DiscoveryClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Sends a discovery request to Helper and waits for the result. The
    /// returned <see cref="DiscoveryOutcome"/> is either a success (carrying the
    /// <see cref="FileDiscoveryResult"/>) or a PHI-safe <see cref="DiscoveryFailure"/>
    /// classifying WHY discovery failed — so the caller can ack the real reason
    /// to the cloud instead of an opaque "see agent logs".
    /// </summary>
    public async Task<DiscoveryOutcome> FindAsync(
        IIpcCommandClient commandClient,
        string jobId,
        FileDiscoverySpec spec,
        CancellationToken ct)
    {
        var request = new IpcRequest(
            Id: Guid.NewGuid().ToString("N"),
            Command: IpcCommands.FindFile,
            Version: 1,
            Data: JsonSerializer.SerializeToElement(new FindFileRequest(jobId, spec)));

        IpcResponse? response;
        try
        {
            response = await commandClient.SendAsync(request, FindTimeout, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            return DiscoveryOutcome.Failed(new DiscoveryFailure(DiscoveryFailureReason.IpcUnavailable));
        }

        if (response is null)
        {
            _logger.LogWarning("core.discovery.helper_timeout");
            return DiscoveryOutcome.Failed(new DiscoveryFailure(DiscoveryFailureReason.HelperTimeout));
        }
        if (response.Id != request.Id)
        {
            _logger.LogWarning("core.discovery.ipc_desync");
            return DiscoveryOutcome.Failed(
                new DiscoveryFailure(DiscoveryFailureReason.IpcDesync, HelperVersionSuspect: true));
        }
        if (response.Status != IpcStatus.Ok)
        {
            _logger.LogWarning("core.discovery.helper_error");
            return DiscoveryOutcome.Failed(new DiscoveryFailure(
                DiscoveryFailureReason.HelperError,
                IpcStatus: response.Status,
                ErrorCode: response.Error?.Code));
        }
        if (response.Data is null)
        {
            _logger.LogWarning("core.discovery.empty_payload");
            return DiscoveryOutcome.Failed(new DiscoveryFailure(DiscoveryFailureReason.NoData));
        }

        try
        {
            var result = JsonSerializer.Deserialize<FileDiscoveryResult>(response.Data.Value);
            if (result is null)
            {
                _logger.LogWarning("core.discovery.deserialize_null");
                return DiscoveryOutcome.Failed(
                    new DiscoveryFailure(DiscoveryFailureReason.DeserializeError, HelperVersionSuspect: true));
            }
            return DiscoveryOutcome.Success(result);
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            return DiscoveryOutcome.Failed(
                new DiscoveryFailure(DiscoveryFailureReason.DeserializeError, HelperVersionSuspect: true));
        }
    }
}
