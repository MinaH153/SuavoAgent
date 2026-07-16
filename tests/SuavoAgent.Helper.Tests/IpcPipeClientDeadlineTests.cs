using System.Diagnostics;
using System.IO.Pipes;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Helper.Tests;

public sealed class IpcPipeClientDeadlineTests
{
    [Fact]
    public async Task SendAsync_UnresponsivePeer_TimesOutAndResetsConnection()
    {
        // Unix maps named pipes to domain-socket paths capped at 104 bytes;
        // retain the full GUID entropy while keeping the cross-platform name bounded.
        var pipeName = $"sa_{Guid.NewGuid():N}";
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var peerRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        // Start accepting before ConnectAsync. The former Task.Run created the
        // server inside a separately scheduled callback, so on Unix the client
        // could observe a missing endpoint and fail immediately. This handshake
        // removes that setup race without changing either deadline.
        var peer = RunUnresponsivePeerAsync(server, peerRead, testCts.Token);

        using var logger = new LoggerConfiguration().CreateLogger();
        using var client = new IpcPipeClient(
            pipeName,
            logger,
            requestTimeout: TimeSpan.FromMilliseconds(150));
        Assert.True(await client.ConnectAsync(TimeSpan.FromSeconds(2), testCts.Token));

        var stopwatch = Stopwatch.StartNew();
        var response = await client.SendAsync(
            new IpcRequest("deadline", IpcCommands.Ping, 1, null),
            testCts.Token);
        stopwatch.Stop();

        Assert.Null(response);
        Assert.False(client.IsConnected);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        await peerRead.Task.WaitAsync(TimeSpan.FromSeconds(1));

        testCts.Cancel();
        try { await peer; }
        catch (OperationCanceledException) { }
    }

    private static async Task RunUnresponsivePeerAsync(
        NamedPipeServerStream server,
        TaskCompletionSource peerRead,
        CancellationToken cancellationToken)
    {
        await server.WaitForConnectionAsync(cancellationToken);
        _ = await IpcFraming.ReadFrameAsync(server, cancellationToken);
        peerRead.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
