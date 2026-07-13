using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Behavioral;

internal sealed class ObservationRuntimeStatusReporter
{
    private readonly IpcPipeClient _ipcClient;
    private readonly CancellationTokenSource _processShutdown;
    private readonly ILogger _logger;

    public ObservationRuntimeStatusReporter(
        IpcPipeClient ipcClient,
        CancellationTokenSource processShutdown,
        ILogger logger)
    {
        _ipcClient = ipcClient;
        _processShutdown = processShutdown;
        _logger = logger;
    }

    public void PersistenceFailed(string code)
    {
        _logger.Error("Observation persistence failed closed ({Code})", code);
        _ = Task.Run(async () =>
        {
            await TryReportAsync(code, IpcCommands.HelperError, CancellationToken.None);
            _processShutdown.Cancel();
        });
    }

    public void Quarantined(string reasonCode)
    {
        _logger.Warning("Observation batch retained in quarantine ({Code})", reasonCode);
        _ = Task.Run(() => TryReportAsync(
            "observation_spool_quarantined",
            IpcCommands.HelperError,
            CancellationToken.None));
    }

    public Task ReportCurrentAsync(string code, CancellationToken cancellationToken) =>
        TryReportAsync(code, IpcCommands.HelperStatus, cancellationToken);

    private async Task TryReportAsync(
        string code,
        string command,
        CancellationToken cancellationToken)
    {
        try
        {
            await _ipcClient.TrySendAsync(
                command,
                JsonSerializer.Serialize(new { code }),
                cancellationToken);
        }
        catch { }
    }
}
