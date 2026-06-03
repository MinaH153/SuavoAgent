using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Ipc;
using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

public class IntentCursorClientTests
{
    [Fact]
    public async Task ShowAsync_SendsIntentCursorCommandWithoutTextFields()
    {
        var ipc = new FakeIpcCommandClient
        {
            ConnectResult = true,
            Response = new IpcResponse(
                "cursor-1",
                IpcStatus.Ok,
                IpcCommands.IntentCursor,
                JsonSerializer.SerializeToElement(new IntentCursorResponse(
                    true,
                    IntentCursorCoordinateSpaces.Screen,
                    900,
                    34,
                    IntentCursorTones.Agent)),
                null),
        };
        var client = new IntentCursorClient(ipc, NullLogger<IntentCursorClient>.Instance);

        var result = await client.ShowAsync(
            new IntentCursorRequest(100, 200, DurationMs: 900),
            CancellationToken.None);

        Assert.True(result.Success);
        var sent = Assert.Single(ipc.Sent);
        Assert.Equal(IpcCommands.IntentCursor, sent.Command);
        Assert.NotNull(sent.Data);
        var raw = sent.Data!.Value.GetRawText();
        Assert.DoesNotContain("text", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("label", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rx", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("patient", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShowAsync_ConnectFailure_DoesNotSend()
    {
        var ipc = new FakeIpcCommandClient { ConnectResult = false };
        var client = new IntentCursorClient(ipc, NullLogger<IntentCursorClient>.Instance);

        var result = await client.ShowAsync(new IntentCursorRequest(100, 200), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ipc_unreachable", result.ErrorCode);
        Assert.Empty(ipc.Sent);
    }

    private sealed class FakeIpcCommandClient : IIpcCommandClient
    {
        public bool ConnectResult { get; set; }
        public IpcResponse? Response { get; set; }
        public List<IpcRequest> Sent { get; } = new();
        public bool IsConnected { get; private set; }

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct)
        {
            IsConnected = ConnectResult;
            return Task.FromResult(ConnectResult);
        }

        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            Sent.Add(request);
            return Task.FromResult(Response);
        }
    }
}
