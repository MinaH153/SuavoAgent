using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper.Workflows;

internal sealed class CoreTop500ProgressSink(
    IpcPipeClient client,
    ILogger logger) : IPioneerRxTop500ExportProgressSink
{
    public void Report(PioneerRxTop500ExportProgress progress)
    {
        try
        {
            var response = client.TrySendAsync(
                    IpcCommands.PricingJobProgress,
                    JsonSerializer.Serialize(progress),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            PioneerRxTop500ProgressReceipt? receipt = null;
            if (response is { Status: IpcStatus.Ok, Error: null, Data: not null })
                receipt = JsonSerializer.Deserialize<PioneerRxTop500ProgressReceipt>(
                    response.Data.Value);
            if (receipt is not
                {
                    Accepted: true,
                } ||
                !string.Equals(receipt.JobId, progress.JobId, StringComparison.Ordinal) ||
                receipt.Sequence != progress.Sequence)
            {
                logger.Warning(
                    "Helper Top-500 progress relay unavailable stage={Stage}",
                    progress.Stage);
            }
        }
        catch (Exception exception)
        {
            logger.Warning(
                "Helper Top-500 progress relay failed stage={Stage} type={ExceptionType}",
                progress.Stage,
                exception.GetType().Name);
        }
    }
}
