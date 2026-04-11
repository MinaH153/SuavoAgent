using Serilog;
using SuavoAgent.Helper;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "logs", "helper-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information("SuavoAgent.Helper starting");

    var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    using var pioneer = new PioneerRxUiaEngine(Log.Logger);
    using var ipcClient = new IpcPipeClient("SuavoAgent", Log.Logger);

    const int maxAttachRetries = 30; // 30 × 10s = 5 minutes of retrying
    int attachFailures = 0;
    bool attached = false;

    // Retry attachment — PioneerRx may not be running when Helper starts
    while (!cts.Token.IsCancellationRequested && !attached)
    {
        if (pioneer.TryAttach())
        {
            attached = true;
            attachFailures = 0;
            Log.Information("Attached to PioneerRx: {Title}", pioneer.WindowTitle);

            // Report success to Core via IPC
            await ipcClient.TrySendAsync("pioneer_attached", null, cts.Token);
        }
        else
        {
            attachFailures++;
            Log.Warning("PioneerRx not found (attempt {Attempt}/{Max})",
                attachFailures, maxAttachRetries);

            // Report persistent failure to Core after threshold
            if (attachFailures % 6 == 0) // every 60 seconds
            {
                await ipcClient.TrySendAsync("pioneer_attach_failed",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        consecutiveFailures = attachFailures,
                        maxRetries = maxAttachRetries
                    }), cts.Token);
            }

            if (attachFailures >= maxAttachRetries)
            {
                Log.Warning("PioneerRx not found after {Max} attempts — exiting", maxAttachRetries);
                await ipcClient.TrySendAsync("pioneer_attach_exhausted",
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        totalAttempts = attachFailures
                    }), cts.Token);
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
        }
    }

    // Health monitoring loop
    while (!cts.Token.IsCancellationRequested && attached)
    {
        var health = pioneer.CheckHealth();
        if (!health.WindowFound)
        {
            Log.Warning("PioneerRx window lost — attempting re-attach");
            attached = false;
            attachFailures = 0;

            // Re-enter attachment loop
            while (!cts.Token.IsCancellationRequested && !attached)
            {
                if (pioneer.TryAttach())
                {
                    attached = true;
                    Log.Information("Re-attached to PioneerRx: {Title}", pioneer.WindowTitle);
                    await ipcClient.TrySendAsync("pioneer_reattached", null, cts.Token);
                }
                else
                {
                    attachFailures++;
                    if (attachFailures >= maxAttachRetries)
                    {
                        Log.Warning("PioneerRx re-attach failed after {Max} attempts", maxAttachRetries);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                }
            }
            if (!attached) break;
        }

        Log.Debug("PioneerRx health: Window={Window}, MenuBar={Menu}",
            health.WindowFound, health.MenuBarFound);
        await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
    }
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    Log.Fatal(ex, "Helper terminated unexpectedly");
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
