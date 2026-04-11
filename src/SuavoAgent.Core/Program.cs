using Serilog;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "logs", "core-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30)
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    Log.Information("SuavoAgent.Core starting v2.0.0");

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "SuavoAgent.Core");
    builder.Services.AddSerilog();

    builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));

    var agentOpts = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
    if (!string.IsNullOrWhiteSpace(agentOpts.ApiKey))
    {
        builder.Services.AddSingleton(new SuavoCloudClient(agentOpts));
    }
    else
    {
        Log.Warning("No ApiKey configured — cloud sync disabled. Set Agent:ApiKey in appsettings.json");
    }

    builder.Services.AddHostedService<HeartbeatWorker>();

    builder.Services.AddSingleton<AgentStateDb>(sp =>
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent");
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "state.db");

        // DPAPI-protected encryption key. No-op with bundle_e_sqlite3,
        // activates when swapped to bundle_e_sqlcipher (no code change needed).
        string? dbPassword = null;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var keyPath = Path.Combine(dataDir, "state.key");
                if (File.Exists(keyPath))
                {
                    var enc = File.ReadAllBytes(keyPath);
                    var dec = System.Security.Cryptography.ProtectedData.Unprotect(
                        enc, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
                    dbPassword = Convert.ToBase64String(dec);
                }
                else
                {
                    var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
                    var enc = System.Security.Cryptography.ProtectedData.Protect(
                        key, null, System.Security.Cryptography.DataProtectionScope.LocalMachine);
                    File.WriteAllBytes(keyPath, enc);
                    dbPassword = Convert.ToBase64String(key);
                    Log.Information("Generated DPAPI-protected database key");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DPAPI key generation failed — state DB unencrypted");
        }

        return new AgentStateDb(dbPath, dbPassword);
    });

    builder.Services.AddSingleton<IpcPipeServer>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<IpcPipeServer>>();
        return new IpcPipeServer("SuavoAgent", msg =>
        {
            logger.LogDebug("IPC: {Command}", msg.Command);
            return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(msg.RequestId, true, "ack", null));
        }, logger);
    });

    builder.Services.AddSingleton<RxDetectionWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RxDetectionWorker>());
    builder.Services.AddHostedService<WritebackProcessor>();

    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "SuavoAgent.Core terminated unexpectedly");
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
