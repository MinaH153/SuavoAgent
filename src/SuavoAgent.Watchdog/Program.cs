using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using SuavoAgent.Diagnostics;
using SuavoAgent.Watchdog;

// Diagnostic Mesh: Wire.AttachUnhandledHooks MUST be the literal first
// executable statement (spec §7 PR 4 wire-ordering invariant; verified
// by WireOrderingTests). Legacy crash sink below kept as defense-in-depth.
Wire.AttachUnhandledHooks(WireComponent.Watchdog, new WireOptions
{
    LocalCrashLogPath = Path.Combine(WatchdogCrashDir(), "watchdog-crash.log"),
    LocalJournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent", "diagnostics", "events.jsonl"),
    Dsn = Environment.GetEnvironmentVariable("SUAVO_SENTRY_DSN"),
    EnableSentry = true,
});

// Crash sink: last-resort unhandled-exception handler that persists to the
// shared SuavoAgent log directory. Same contract as Broker/Core — the service
// must leave an audit trail even when the host dies before Serilog is ready.
// Kept alongside Wire so a Wire-init failure still leaves a plaintext audit.
static string WatchdogCrashDir()
{
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var dir = Path.Combine(programData, "SuavoAgent", "logs");
    try { Directory.CreateDirectory(dir); } catch { }
    return dir;
}
static void WriteWatchdogCrash(string stage, Exception ex)
{
    try
    {
        var line = $"[{DateTimeOffset.Now:O}] [{stage}] {ex.GetType().FullName}: {ex.Message}"
                   + Environment.NewLine + ex.ToString() + Environment.NewLine + Environment.NewLine;
        File.AppendAllText(
            Path.Combine(WatchdogCrashDir(), "watchdog-crash.log"),
            line,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
    catch { }
}

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    WriteWatchdogCrash("UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
TaskScheduler.UnobservedTaskException += (_, e) =>
    WriteWatchdogCrash("UnobservedTaskException", e.Exception);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "logs", "watchdog-.log"),
        encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information("SuavoAgent.Watchdog starting — account={Account}, pid={Pid}",
        Environment.UserName, Environment.ProcessId);
    Log.Information("IsWindowsService={IsService}", WindowsServiceHelpers.IsWindowsService());

    // Empty builder: Watchdog has no configuration file dependency. Mirrors
    // Broker's pattern to avoid auto-loading appsettings.json from a dir whose
    // ACL may deny the service account access.
    var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = exeDir,
    });
    builder.Services.AddWindowsService(options => options.ServiceName = "SuavoAgent.Watchdog");
    builder.Services.AddSerilog();

    builder.Services.AddSingleton<IServiceCommand, ServiceCommand>();
    builder.Services.AddSingleton(sp =>
    {
        var bootstrap = Environment.GetEnvironmentVariable("SUAVO_BOOTSTRAP_PS1")
                        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                        "SuavoAgent", "bootstrap.ps1");
        // Restart-request lives in the install dir (alongside the OTA sentinel), whose ACL
        // denies interactive-user writes — Core writes it post-swap, the Watchdog consumes it.
        return new WatchdogOptions
        {
            BootstrapPath = bootstrap,
            RestartRequestPath = Path.Combine(exeDir, "watchdog-restart-request.json"),
        };
    });
    builder.Services.AddHostedService<WatchdogWorker>();

    var host = builder.Build();
    Log.Information("Watchdog host built — running");
    host.Run();
}
catch (Exception ex)
{
    try { Log.Fatal(ex, "Watchdog terminated unexpectedly"); } catch { }
    WriteWatchdogCrash("Main", ex);
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
