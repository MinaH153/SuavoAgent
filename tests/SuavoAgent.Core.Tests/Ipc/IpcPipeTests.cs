using System.IO.Pipes;
using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

public class IpcPipeTests
{
    [Fact]
    public async Task Server_And_Client_Exchange_Messages()
    {
        var pipeName = $"SuavoTest_{Guid.NewGuid():N}";
        var logger = NullLogger<IpcPipeServer>.Instance;

        // Server handler echoes back
        var server = new IpcPipeServer(pipeName, msg =>
        {
            return Task.FromResult(new IpcResponse(msg.Id, IpcStatus.Ok, msg.Command, null, null));
        }, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        // Give server time to start listening
        await Task.Delay(100);

        // Client connects and sends using framed protocol
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(5000, cts.Token);

        var request = new IpcRequest("req-001", IpcCommands.Ping, 1, null);
        var requestJson = JsonSerializer.Serialize(request);
        await IpcFraming.WriteFrameAsync(clientPipe, requestJson, cts.Token);

        var responseJson = await IpcFraming.ReadFrameAsync(clientPipe, cts.Token);
        Assert.NotNull(responseJson);

        var response = JsonSerializer.Deserialize<IpcResponse>(responseJson);
        Assert.NotNull(response);
        Assert.Equal(IpcStatus.Ok, response.Status);
        Assert.Equal("req-001", response.Id);
        Assert.Equal(IpcCommands.Ping, response.Command);

        server.Dispose();
    }

    [Fact]
    public async Task Server_Reports_Connected_Status()
    {
        var pipeName = $"SuavoTest_{Guid.NewGuid():N}";
        var logger = NullLogger<IpcPipeServer>.Instance;

        var server = new IpcPipeServer(pipeName, msg =>
            Task.FromResult(new IpcResponse(msg.Id, IpcStatus.Ok, msg.Command, null, null)), logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        Assert.False(server.IsConnected);

        // Connect
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(5000, cts.Token);

        await Task.Delay(200); // Let server process
        Assert.True(server.IsConnected);

        clientPipe.Close();
        await Task.Delay(500); // Let server detect disconnect
        Assert.False(server.IsConnected);

        server.Dispose();
    }
}
