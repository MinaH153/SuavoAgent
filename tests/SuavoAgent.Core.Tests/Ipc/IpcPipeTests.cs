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
    public void Server_Can_Be_Constructed()
    {
        var logger = NullLogger<IpcPipeServer>.Instance;
        using var server = new IpcPipeServer("SuavoTest_Smoke", msg =>
            Task.FromResult(new IpcResponse(msg.Id, IpcStatus.Ok, msg.Command, null, null)), logger);

        Assert.NotNull(server);
        Assert.Equal("SuavoTest_Smoke", server.PipeName);
        Assert.False(server.IsConnected);
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

        // Poll for the server to mark itself connected. 200ms was tight on
        // shared CI runners; the poll bails fast in the happy path while
        // tolerating up to 3s of scheduler jitter under load.
        await WaitForAsync(() => server.IsConnected, TimeSpan.FromSeconds(3), cts.Token);
        Assert.True(server.IsConnected);

        clientPipe.Close();
        await WaitForAsync(() => !server.IsConnected, TimeSpan.FromSeconds(3), cts.Token);
        Assert.False(server.IsConnected);

        server.Dispose();
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(50, cancellationToken);
        }
    }
}
