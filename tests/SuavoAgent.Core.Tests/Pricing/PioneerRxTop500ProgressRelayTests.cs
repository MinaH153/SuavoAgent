using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PioneerRxTop500ProgressRelayTests
{
    [Fact]
    public async Task RegisteredJob_ReceivesTruthfulHelperGeneratingAndExportingStages()
    {
        var jobId = Guid.NewGuid().ToString("D");
        var relay = new PioneerRxTop500ProgressRelay();
        var received = new List<PioneerRxTop500ExportProgress>();
        using var lease = relay.TryRegister(
            jobId,
            (progress, _) =>
            {
                received.Add(progress);
                return ValueTask.CompletedTask;
            });
        Assert.NotNull(lease);

        var generating = await SendAsync(
            relay,
            new PioneerRxTop500ExportProgress(
                jobId,
                PioneerRxTop500ExportStages.GeneratingReportSequence,
                PioneerRxTop500ExportStages.GeneratingReport,
                0,
                0,
                0,
                DateTimeOffset.UtcNow));
        var exporting = await SendAsync(
            relay,
            new PioneerRxTop500ExportProgress(
                jobId,
                PioneerRxTop500ExportStages.ExportingReportSequence,
                PioneerRxTop500ExportStages.ExportingReport,
                0,
                0,
                0,
                DateTimeOffset.UtcNow));

        Assert.Equal(IpcStatus.Ok, generating.Status);
        Assert.Equal(IpcStatus.Ok, exporting.Status);
        Assert.Equal(
            [
                PioneerRxTop500ExportStages.GeneratingReport,
                PioneerRxTop500ExportStages.ExportingReport,
            ],
            received.Select(progress => progress.Stage));
    }

    [Fact]
    public async Task UnregisteredOrWrongSequenceProgress_IsRejected()
    {
        var jobId = Guid.NewGuid().ToString("D");
        var relay = new PioneerRxTop500ProgressRelay();
        using var lease = relay.TryRegister(
            jobId,
            (_, _) => ValueTask.CompletedTask);

        var response = await SendAsync(
            relay,
            new PioneerRxTop500ExportProgress(
                jobId,
                9,
                PioneerRxTop500ExportStages.GeneratingReport,
                0,
                0,
                0,
                DateTimeOffset.UtcNow));

        Assert.Equal(IpcStatus.BadRequest, response.Status);
    }

    private static Task<IpcResponse> SendAsync(
        PioneerRxTop500ProgressRelay relay,
        PioneerRxTop500ExportProgress progress) =>
        PioneerRxTop500ProgressIpcProcessor.ProcessAsync(
            new IpcRequest(
                Guid.NewGuid().ToString("N"),
                IpcCommands.PricingJobProgress,
                1,
                JsonSerializer.SerializeToElement(progress)),
            relay,
            CancellationToken.None);
}
