using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;
using SuavoAgent.Contracts.Maintenance;
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

// Crash sink: last-resort unhandled-exception handler that persists a PHI-safe
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
static string SafeExceptionType(Exception? exception)
{
    var name = exception?.GetType().Name ?? "NonExceptionFailure";
    return new string(name
        .Take(96)
        .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_')
        .ToArray());
}
static string SafeCrashStage(string stage) => stage switch
{
    "UnhandledException" => "unhandled_exception",
    "UnobservedTaskException" => "unobserved_task_exception",
    "Main" => "main",
    _ => "unknown",
};
static void WriteWatchdogCrash(string stage, Exception? exception)
{
    try
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] code=watchdog.{SafeCrashStage(stage)}.fatal " +
                   $"exception_type={SafeExceptionType(exception)}{Environment.NewLine}";
        File.AppendAllText(
            Path.Combine(WatchdogCrashDir(), "watchdog-crash.log"),
            line,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
    catch { }
}

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    WriteWatchdogCrash("UnhandledException", e.ExceptionObject as Exception);
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
    Log.Information("watchdog.process_started");
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
        var updateRoot = UpdateActivationContract.DefaultUpdateRoot();
        var maintenanceRoot = UpdateActivationContract.DefaultMaintenanceRoot();
        var identity = WatchdogInstallIdentityReader.TryRead(exeDir);
        // Core writes the signed repair handoff in its LocalService data root. Watchdog treats
        // that file as untrusted and independently verifies identity, freshness, payload hash,
        // and the production command signature before privileged maintenance can run.
        return new WatchdogOptions
        {
            UpdateRoot = updateRoot,
            ActivationRequestPath = Path.Combine(
                updateRoot,
                UpdateActivationContract.ActivationRequestFileName),
            ReplayLedgerPath = Path.Combine(
                updateRoot,
                UpdateActivationContract.CoordinatorDirectoryName,
                UpdateActivationContract.ReplayLedgerFileName),
            ExpectedAgentId = identity?.AgentId,
            ExpectedMachineFingerprint = identity?.MachineFingerprint,
            CurrentVersion = identity?.Version,
            MaintenanceRoot = maintenanceRoot,
            RepairRequestPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent",
                RemoteRepairContract.RequestFileName),
            RemoteRepairReplayLedgerPath = Path.Combine(
                maintenanceRoot,
                RemoteRepairContract.ReplayLedgerFileName),
            PioneerRxApprovalRequestPath =
                PioneerRxApprovalMaintenanceContract.DefaultRequestPath(),
            PioneerRxApprovalBootstrapRequestPath =
                PioneerRxApprovalBootstrapContract.DefaultRequestPath(),
            ActiveClaimPath = Path.Combine(
                maintenanceRoot,
                UpdateActivationContract.ActiveClaimFileName),
            ActivationCompletionPath = Path.Combine(
                maintenanceRoot,
                UpdateActivationContract.CompletionFileName),
        };
    });
    builder.Services.AddHostedService<WatchdogWorker>();

    var host = builder.Build();
    Log.Information("Watchdog host built — running");
    host.Run();
}
catch (Exception ex)
{
    try
    {
        Log.Fatal(
            "watchdog.main.fatal exception_type={ExceptionType}",
            SafeExceptionType(ex));
    }
    catch { }
    WriteWatchdogCrash("Main", ex);
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
