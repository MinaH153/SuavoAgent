using System.Text.Json;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.Ipc;

public sealed record IntentCursorClientResult(
    bool Success,
    string? ErrorCode,
    IntentCursorResponse? Response);

public interface IIntentCursorClient
{
    Task<IntentCursorClientResult> ShowAsync(IntentCursorRequest request, CancellationToken ct);
}

public sealed class IntentCursorClient : IIntentCursorClient
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    private readonly IIpcCommandClient _ipc;
    private readonly ILogger<IntentCursorClient> _logger;

    public IntentCursorClient(IIpcCommandClient ipc, ILogger<IntentCursorClient> logger)
    {
        _ipc = ipc;
        _logger = logger;
    }

    public async Task<IntentCursorClientResult> ShowAsync(IntentCursorRequest request, CancellationToken ct)
    {
        if (!_ipc.IsConnected)
        {
            var connected = await _ipc.ConnectAsync(ConnectTimeout, ct).ConfigureAwait(false);
            if (!connected)
            {
                return new IntentCursorClientResult(false, "ipc_unreachable", null);
            }
        }

        var ipcRequest = new IpcRequest(
            Id: Guid.NewGuid().ToString("N"),
            Command: IpcCommands.IntentCursor,
            Version: 1,
            Data: JsonSerializer.SerializeToElement(request));

        var response = await _ipc.SendAsync(ipcRequest, SendTimeout, ct).ConfigureAwait(false);
        if (response is null)
        {
            return new IntentCursorClientResult(false, "ipc_timeout", null);
        }

        if (response.Status != IpcStatus.Ok)
        {
            return new IntentCursorClientResult(
                false,
                response.Error?.Code ?? "intent_cursor_rejected",
                null);
        }

        if (response.Data is null)
        {
            return new IntentCursorClientResult(false, "missing_response", null);
        }

        try
        {
            var data = response.Data.Value.Deserialize<IntentCursorResponse>();
            return data is null
                ? new IntentCursorClientResult(false, "missing_response", null)
                : new IntentCursorClientResult(true, null, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IntentCursorClient received malformed response");
            return new IntentCursorClientResult(false, "malformed_response", null);
        }
    }
}
