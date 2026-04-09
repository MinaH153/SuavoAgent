using System.IO.Pipes;
using System.Text;
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
            return Task.FromResult(new IpcResponse(msg.RequestId, true, $"echo:{msg.Command}", null));
        }, logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        // Give server time to start listening
        await Task.Delay(100);

        // Client connects and sends
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(5000, cts.Token);

        using var writer = new StreamWriter(clientPipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(clientPipe, Encoding.UTF8, leaveOpen: true);

        var msg = new IpcMessage(1, "req-001", IpcCommands.Ping, null);
        await writer.WriteLineAsync(JsonSerializer.Serialize(msg));

        var responseLine = await reader.ReadLineAsync(cts.Token);
        Assert.NotNull(responseLine);

        var response = JsonSerializer.Deserialize<IpcResponse>(responseLine);
        Assert.NotNull(response);
        Assert.True(response.Success);
        Assert.Equal("req-001", response.RequestId);
        Assert.Equal("echo:ping", response.Result);

        server.Dispose();
    }

    [Fact]
    public async Task Server_Reports_Connected_Status()
    {
        var pipeName = $"SuavoTest_{Guid.NewGuid():N}";
        var logger = NullLogger<IpcPipeServer>.Instance;

        var server = new IpcPipeServer(pipeName, msg =>
            Task.FromResult(new IpcResponse(msg.RequestId, true, "ok", null)), logger);

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
