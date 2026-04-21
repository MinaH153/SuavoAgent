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
    /// Sends a discovery request to Helper and waits for the result. Null
    /// return = Helper unreachable, IPC timeout, or Helper returned an
    /// error — caller treats as discovery failure (operator must supply
    /// the path manually).
    /// </summary>
    public async Task<FileDiscoveryResult?> FindAsync(
        IpcCommandClient commandClient,
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
            _logger.LogWarning(ex, "DiscoveryClient: IPC send failed for job {JobId}", jobId);
            return null;
        }

        if (response is null)
        {
            _logger.LogWarning("DiscoveryClient: no response from Helper for job {JobId}", jobId);
            return null;
        }
        if (response.Id != request.Id)
        {
            _logger.LogWarning(
                "DiscoveryClient: response id mismatch (sent {Sent} got {Got}) — pipe desync guard",
                request.Id, response.Id);
            return null;
        }
        if (response.Status != IpcStatus.Ok)
        {
            _logger.LogWarning(
                "DiscoveryClient: Helper returned {Status} {Code}: {Message}",
                response.Status, response.Error?.Code, response.Error?.Message);
            return null;
        }
        if (response.Data is null)
        {
            _logger.LogWarning("DiscoveryClient: Helper returned OK with no data for job {JobId}", jobId);
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FileDiscoveryResult>(response.Data.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DiscoveryClient: failed to deserialize FileDiscoveryResult");
            return null;
        }
    }
}
