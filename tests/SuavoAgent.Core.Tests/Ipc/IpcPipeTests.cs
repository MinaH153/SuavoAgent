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

    [Fact]
    public async Task ThrowingHandler_ReturnsRetryableFailure_ThenClosesConnection()
    {
        var pipeName = $"SuavoTest_{Guid.NewGuid():N}";
        using var server = new IpcPipeServer(
            pipeName,
            _ => throw new InvalidOperationException("simulated persistence failure"),
            NullLogger<IpcPipeServer>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);

        using var clientPipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(5000, cts.Token);
        await IpcFraming.WriteFrameAsync(
            clientPipe,
            JsonSerializer.Serialize(new IpcRequest("failed-request", IpcCommands.Ping, 1, null)),
            cts.Token);

        var responseJson = await IpcFraming.ReadFrameAsync(clientPipe, cts.Token);
        var response = JsonSerializer.Deserialize<IpcResponse>(responseJson!);

        Assert.NotNull(response);
        Assert.Equal(IpcStatus.InternalError, response!.Status);
        Assert.Equal("handler_failed", response.Error!.Code);
        Assert.True(response.Error.Retryable);
        await WaitForAsync(() => !server.IsConnected, TimeSpan.FromSeconds(3), cts.Token);
        Assert.False(server.IsConnected);
    }

    // --- Chunk 2: SendAsync reconnect-on-demand (transient Helper drop self-recovers) ---

    private static Task<IpcResponse> Echo(IpcRequest msg)
        => Task.FromResult(new IpcResponse(msg.Id, IpcStatus.Ok, msg.Command, null, null));

    [Fact]
    public async Task SendAsync_ConnectsOnDemand_WhenNeverConnected()
    {
        var pipeName = $"SuavoTest_{Guid.NewGuid():N}";
        using var server = new IpcPipeServer(pipeName, Echo, NullLogger<IpcPipeServer>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        server.Start(cts.Token);
        await Task.Delay(100);

        await using var client = new IpcCommandClient(pipeName, NullLogger<IpcCommandClient>.Instance);

        // ConnectAsync was never called — _pipe is null. SendAsync must connect on demand.
        var resp = await client.SendAsync(
            new IpcRequest("r1", IpcCommands.Ping, 1, null), TimeSpan.FromSeconds(5), cts.Token);

        Assert.NotNull(resp);
        Assert.Equal(IpcStatus.Ok, resp!.Status);
        Assert.Equal("r1", resp.Id);
    }

    [Fact]
    public async Task Connect_sends_vision_identity_before_first_application_command()
    {
        var pipeName = $"SuavoTest_{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var identity = new VisionStateHandshake(1, 7, new string('a', 64));
        var sequence = new List<string>();
        Task<IpcResponse> Handler(IpcRequest request)
        {
            sequence.Add(request.Command);
            return Task.FromResult(request.Command == IpcCommands.VisionStateHandshake
                ? new IpcResponse(
                    request.Id,
                    IpcStatus.Ok,
                    request.Command,
                    JsonSerializer.SerializeToElement(new
                    {
                        matched = true,
                        generation = identity.Generation,
                        configDigest = identity.ConfigDigest,
                    }),
                    null)
                : new IpcResponse(request.Id, IpcStatus.Ok, request.Command, null, null));
        }
        using var server = new IpcPipeServer(
            pipeName,
            Handler,
            NullLogger<IpcPipeServer>.Instance);
        server.Start(cts.Token);
        await Task.Delay(100, cts.Token);
        await using var client = new IpcCommandClient(
            pipeName,
            NullLogger<IpcCommandClient>.Instance,
            identity);

        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(5), cts.Token));
        var response = await client.SendAsync(
            new IpcRequest("after", IpcCommands.Ping, 1, null),
            TimeSpan.FromSeconds(5),
            cts.Token);

        Assert.NotNull(response);
        Assert.Equal(
            new[] { IpcCommands.VisionStateHandshake, IpcCommands.Ping },
            sequence);
    }

    [Fact]
    public async Task Connect_fails_closed_when_Helper_confirms_a_different_generation()
    {
        var pipeName = $"SuavoTest_{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var identity = new VisionStateHandshake(1, 7, new string('a', 64));
        Task<IpcResponse> Mismatch(IpcRequest request) => Task.FromResult(new IpcResponse(
            request.Id,
            IpcStatus.Ok,
            request.Command,
            JsonSerializer.SerializeToElement(new
            {
                matched = true,
                generation = 8,
                configDigest = identity.ConfigDigest,
            }),
            null));
        using var server = new IpcPipeServer(
            pipeName,
            Mismatch,
            NullLogger<IpcPipeServer>.Instance);
        server.Start(cts.Token);
        await Task.Delay(100, cts.Token);
        await using var client = new IpcCommandClient(
            pipeName,
            NullLogger<IpcCommandClient>.Instance,
            identity);

        Assert.False(await client.ConnectAsync(TimeSpan.FromSeconds(5), cts.Token));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task SendAsync_Reconnects_AfterServerRestart()
    {
        var pipeName = $"SuavoTest_{Guid.NewGuid():N}";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var server1 = new IpcPipeServer(pipeName, Echo, NullLogger<IpcPipeServer>.Instance);
        server1.Start(cts.Token);
        await Task.Delay(100);

        await using var client = new IpcCommandClient(pipeName, NullLogger<IpcCommandClient>.Instance);
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(5), cts.Token));
        var first = await client.SendAsync(
            new IpcRequest("r1", IpcCommands.Ping, 1, null), TimeSpan.FromSeconds(5), cts.Token);
        Assert.NotNull(first);

        // Drop: kill the server so the client's pipe breaks, then bring a fresh
        // server up on the same name. SendAsync must self-recover (broken send
        // tears down the stale pipe; the next send reconnects on demand).
        server1.Dispose();
        using var server2 = new IpcPipeServer(pipeName, Echo, NullLogger<IpcPipeServer>.Instance);
        server2.Start(cts.Token);
        await Task.Delay(100);

        IpcResponse? recovered = null;
        for (var i = 0; i < 3 && recovered == null; i++)
        {
            recovered = await client.SendAsync(
                new IpcRequest($"r-{i}", IpcCommands.Ping, 1, null), TimeSpan.FromSeconds(5), cts.Token);
        }

        Assert.NotNull(recovered);
        Assert.Equal(IpcStatus.Ok, recovered!.Status);
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
