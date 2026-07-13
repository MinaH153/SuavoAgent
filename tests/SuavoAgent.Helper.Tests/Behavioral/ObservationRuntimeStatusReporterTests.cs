using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.Behavioral;
using Xunit;

namespace SuavoAgent.Helper.Tests.Behavioral;

public sealed class ObservationRuntimeStatusReporterTests
{
    [Fact]
    public async Task ReportsClosedCodesAndCancelsHelperAfterPersistenceFailure()
    {
        var pipeName = $"sa_status_{Guid.NewGuid():N}";
        var received = new ConcurrentQueue<IpcRequest>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var peer = RunPeerAsync(server, received, expected: 3, timeout.Token);
        using var logger = new LoggerConfiguration().CreateLogger();
        using var client = new IpcPipeClient(
            pipeName,
            logger,
            requestTimeout: TimeSpan.FromSeconds(1));
        using var helperShutdown = new CancellationTokenSource();
        var reporter = new ObservationRuntimeStatusReporter(
            client,
            helperShutdown,
            logger);

        await reporter.ReportCurrentAsync(
            "observation_ready",
            timeout.Token);
        reporter.Quarantined("batch_signature_invalid");
        await WaitUntilAsync(() => received.Count >= 2, timeout.Token);
        reporter.PersistenceFailed("observation_spool_write_failed");
        await WaitUntilAsync(() => helperShutdown.IsCancellationRequested, timeout.Token);
        await peer;

        var requests = received.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Contains(requests, request =>
            request.Command == IpcCommands.HelperStatus &&
            Code(request) == "observation_ready");
        Assert.Contains(requests, request =>
            request.Command == IpcCommands.HelperError &&
            Code(request) == "observation_spool_quarantined");
        Assert.Contains(requests, request =>
            request.Command == IpcCommands.HelperError &&
            Code(request) == "observation_spool_write_failed");
    }

    private static string? Code(IpcRequest request) =>
        request.Data?.GetProperty("code").GetString();

    private static async Task RunPeerAsync(
        NamedPipeServerStream server,
        ConcurrentQueue<IpcRequest> received,
        int expected,
        CancellationToken cancellationToken)
    {
        await server.WaitForConnectionAsync(cancellationToken);
        for (var index = 0; index < expected; index++)
        {
            var json = await IpcFraming.ReadFrameAsync(server, cancellationToken);
            Assert.NotNull(json);
            var request = JsonSerializer.Deserialize<IpcRequest>(json!);
            Assert.NotNull(request);
            received.Enqueue(request!);
            var response = JsonSerializer.Serialize(new IpcResponse(
                request!.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(new { acknowledged = true }),
                null));
            await IpcFraming.WriteFrameAsync(server, response, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        CancellationToken cancellationToken)
    {
        while (!predicate())
            await Task.Delay(10, cancellationToken);
    }
}
