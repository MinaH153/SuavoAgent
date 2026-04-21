using Serilog;
using SuavoAgent.Broker;

// Crash sink: wire a last-resort handler that persists unhandled exceptions
// to C:\ProgramData\SuavoAgent\logs\broker-crash.log even when the process
// dies before .NET's exception machinery or Serilog are ready. Without
// this, failures under NetworkService are invisible (no Application event,
// WIN32_EXIT_CODE reports 0, SCM just times out).
static string BrokerCrashDir()
{
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var dir = Path.Combine(programData, "SuavoAgent", "logs");
    try { Directory.CreateDirectory(dir); } catch { }
    return dir;
}
static void WriteBrokerCrash(string stage, Exception ex)
{
    try
    {
        var line = $"[{DateTimeOffset.Now:O}] [{stage}] {ex.GetType().FullName}: {ex.Message}"
                   + Environment.NewLine + ex.ToString() + Environment.NewLine + Environment.NewLine;
        File.AppendAllText(Path.Combine(BrokerCrashDir(), "broker-crash.log"), line);
    }
    catch { }
}

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    WriteBrokerCrash("UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
TaskScheduler.UnobservedTaskException += (_, e) =>
    WriteBrokerCrash("UnobservedTaskException", e.Exception);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "logs", "broker-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information("SuavoAgent.Broker starting — account={Account}, sessionId={Session}",
        Environment.UserName, Environment.ProcessId);
    Log.Information("IsWindowsService={IsService}",
        Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());

    // Pin ContentRootPath to the exe directory — SCM launches services with
    // CWD=C:\Windows\System32 otherwise, so any relative file resolution in
    // the host's default config providers misses the install dir.
    var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = exeDir,
    });
    builder.Services.AddWindowsService(options => options.ServiceName = "SuavoAgent.Broker");
    builder.Services.AddSerilog();
    builder.Services.AddHostedService<SessionWatcher>();
    var host = builder.Build();
    Log.Information("Broker host built — running");
    host.Run();
}
catch (Exception ex)
{
    try { Log.Fatal(ex, "Broker terminated unexpectedly"); } catch { }
    WriteBrokerCrash("Main", ex);
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
