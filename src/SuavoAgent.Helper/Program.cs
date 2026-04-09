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

    if (pioneer.TryAttach())
    {
        Log.Information("Attached to PioneerRx: {Title}", pioneer.WindowTitle);

        // Keep running and report status
        while (!cts.Token.IsCancellationRequested)
        {
            var health = pioneer.CheckHealth();
            Log.Debug("PioneerRx health: Window={Window}, MenuBar={Menu}",
                health.WindowFound, health.MenuBarFound);
            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
        }
    }
    else
    {
        Log.Warning("PioneerRx not found — Helper will exit");
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
