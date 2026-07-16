using Serilog;
using SuavoAgent.Broker;
using SuavoAgent.Diagnostics;

// Diagnostic Mesh: Wire.AttachUnhandledHooks MUST be the literal first
// executable statement (spec §7 PR 4 wire-ordering invariant; verified
// by WireOrderingTests). Legacy crash sink below kept as defense-in-depth.
Wire.AttachUnhandledHooks(WireComponent.Broker, new WireOptions
{
    LocalCrashLogPath = Path.Combine(BrokerCrashDir(), "broker-crash.log"),
    LocalJournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent", "diagnostics", "events.jsonl"),
    Dsn = Environment.GetEnvironmentVariable("SUAVO_SENTRY_DSN"),
    EnableSentry = true,
});

// Crash sink: wire a last-resort handler that persists PHI-safe structural failures
// to C:\ProgramData\SuavoAgent\logs\broker-crash.log even when the process
// dies before .NET's exception machinery or Serilog are ready. Kept
// alongside Wire so a Wire-init failure still leaves a plaintext audit.
static string BrokerCrashDir()
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
static void WriteBrokerCrash(string stage, Exception? exception)
{
    try
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] code=broker.{SafeCrashStage(stage)}.fatal " +
                   $"exception_type={SafeExceptionType(exception)}{Environment.NewLine}";
        File.AppendAllText(
            Path.Combine(BrokerCrashDir(), "broker-crash.log"),
            line,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
    catch { }
}

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    WriteBrokerCrash("UnhandledException", e.ExceptionObject as Exception);
TaskScheduler.UnobservedTaskException += (_, e) =>
    WriteBrokerCrash("UnobservedTaskException", e.Exception);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "logs", "broker-.log"),
        encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    Log.Information("broker.process_started");
    Log.Information("IsWindowsService={IsService}",
        Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());

    // Use CreateEmptyApplicationBuilder so the host does NOT auto-load
    // appsettings.json from the install dir. Broker has no configuration
    // needs, and the appsettings ACL is restricted to SYSTEM/Admins/
    // LocalService only (it holds Core's DB credentials) — if we touch it
    // from the NetworkService account we get an UnauthorizedAccessException
    // inside the Host ctor before the try/catch/crash-sink can catch it.
    var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = exeDir,
    });
    builder.Services.AddWindowsService(options => options.ServiceName = "SuavoAgent.Broker");
    builder.Services.AddSerilog();
    builder.Services.AddHostedService<SessionWatcher>();
    // Honeytoken attribution oracle (LocalSystem-only ETW). Self-gates on OperatingSystem.IsWindows() and
    // fail-opens on any ETW error, so unconditional registration is safe on non-Windows CI/dev. Inert until
    // the Helper is flipped to HoneytokenAttributorMode=EtwBroker (the doc is just written + ignored otherwise).
    builder.Services.AddHostedService<SuavoAgent.Broker.Honeytoken.EtwHoneytokenFileTracer>();
    var host = builder.Build();
    Log.Information("Broker host built — running");
    host.Run();
}
catch (Exception ex)
{
    try
    {
        Log.Fatal(
            "broker.main.fatal exception_type={ExceptionType}",
            SafeExceptionType(ex));
    }
    catch { }
    WriteBrokerCrash("Main", ex);
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
