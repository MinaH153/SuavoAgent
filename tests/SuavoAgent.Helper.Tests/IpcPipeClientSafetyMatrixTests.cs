using System.IO.Pipes;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Helper.Tests;

public sealed class IpcPipeClientSafetyMatrixTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonPositiveRequestDeadline(int milliseconds)
    {
        using var logger = new LoggerConfiguration().CreateLogger();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new IpcPipeClient(
                "pipe",
                logger,
                TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task DisconnectedAndDisposedClientNeverPretendsToSend()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var client = new IpcPipeClient("missing_pipe", logger);

        Assert.Null(await client.SendAsync(Request("disconnected"), CancellationToken.None));
        Assert.False(client.IsConnected);
        client.Dispose();
        client.Dispose();

        Assert.False(await client.ConnectAsync(
            TimeSpan.FromMilliseconds(10),
            CancellationToken.None));
    }

    [Fact]
    public async Task ConnectToMissingPipeReturnsFalseWithoutLeakingException()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        using var client = new IpcPipeClient(
            $"sa_missing_{Guid.NewGuid():N}",
            logger);

        var connected = await client.ConnectAsync(
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        Assert.False(connected);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task AlreadyConnectedClientDoesNotOpenSecondEndpoint()
    {
        var pipeName = $"sa_ipc_{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = Server(pipeName);
        var accepting = server.WaitForConnectionAsync(timeout.Token);
        using var logger = new LoggerConfiguration().CreateLogger();
        using var client = new IpcPipeClient(pipeName, logger);

        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2), timeout.Token));
        await accepting;
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2), timeout.Token));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task PingRoundTripsTypedResponse()
    {
        var pipeName = $"sa_ipc_{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = Server(pipeName);
        var peer = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            var frame = await IpcFraming.ReadFrameAsync(server, timeout.Token);
            var request = JsonSerializer.Deserialize<IpcRequest>(frame!);
            Assert.NotNull(request);
            Assert.Equal(IpcCommands.Ping, request!.Command);
            await IpcFraming.WriteFrameAsync(
                server,
                JsonSerializer.Serialize(new IpcResponse(
                    request.Id,
                    IpcStatus.Ok,
                    request.Command,
                    null,
                    null)),
                timeout.Token);
        }, timeout.Token);
        using var logger = new LoggerConfiguration().CreateLogger();
        using var client = new IpcPipeClient(pipeName, logger);
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2), timeout.Token));

        var response = await client.PingAsync(timeout.Token);

        Assert.NotNull(response);
        Assert.Equal(IpcStatus.Ok, response!.Status);
        Assert.Equal(IpcCommands.Ping, response.Command);
        await peer;
    }

    [Fact]
    public async Task MalformedResponseFailsClosedAndResetsConnection()
    {
        var pipeName = $"sa_ipc_{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = Server(pipeName);
        var peer = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(timeout.Token);
            _ = await IpcFraming.ReadFrameAsync(server, timeout.Token);
            await IpcFraming.WriteFrameAsync(server, "not-json", timeout.Token);
        }, timeout.Token);
        using var logger = new LoggerConfiguration().CreateLogger();
        using var client = new IpcPipeClient(pipeName, logger);
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2), timeout.Token));

        var response = await client.SendAsync(Request("malformed"), timeout.Token);

        Assert.Null(response);
        Assert.False(client.IsConnected);
        await peer;
    }

    [Fact]
    public async Task CallerCancellationPropagatesAndResetsConnection()
    {
        var pipeName = $"sa_ipc_{Guid.NewGuid():N}";
        using var outerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = Server(pipeName);
        var requestRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var peer = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(outerTimeout.Token);
            _ = await IpcFraming.ReadFrameAsync(server, outerTimeout.Token);
            requestRead.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, outerTimeout.Token);
        }, outerTimeout.Token);
        using var logger = new LoggerConfiguration().CreateLogger();
        using var client = new IpcPipeClient(
            pipeName,
            logger,
            requestTimeout: TimeSpan.FromSeconds(3));
        Assert.True(await client.ConnectAsync(
            TimeSpan.FromSeconds(2),
            outerTimeout.Token));
        using var caller = new CancellationTokenSource();
        var sending = client.SendAsync(Request("caller_cancel"), caller.Token);
        await requestRead.Task.WaitAsync(outerTimeout.Token);

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        Assert.False(client.IsConnected);

        outerTimeout.Cancel();
        try { await peer; }
        catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task InvalidBestEffortPayloadReturnsNullWithoutSendingFrame()
    {
        var pipeName = $"sa_ipc_{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var server = Server(pipeName);
        var accepting = server.WaitForConnectionAsync(timeout.Token);
        using var logger = new LoggerConfiguration().CreateLogger();
        using var client = new IpcPipeClient(pipeName, logger);
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2), timeout.Token));
        await accepting;

        var response = await client.TrySendAsync(
            IpcCommands.HelperStatus,
            "not-json",
            timeout.Token);

        Assert.Null(response);
        Assert.True(client.IsConnected);
    }

    private static IpcRequest Request(string id) => new(
        id,
        IpcCommands.Ping,
        1,
        null);

    private static NamedPipeServerStream Server(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);
}
