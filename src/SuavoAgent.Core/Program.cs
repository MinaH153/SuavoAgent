using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Core;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Workers;

// Bootstrap self-update — runs before any DI/config
{
    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SuavoAgent");
    Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

    using var serilogLogger = new LoggerConfiguration()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(dataDir, "logs", "startup-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7)
        .CreateLogger();

    using var earlyLogFactory = LoggerFactory.Create(lb => lb.AddSerilog(serilogLogger));
    var earlyLog = earlyLogFactory.CreateLogger("SuavoAgent.Bootstrap");

    if (SuavoAgent.Core.Cloud.SelfUpdater.CheckPendingUpdate(earlyLog))
    {
        serilogLogger.Information("Bootstrap update applied — restarting");
        Environment.Exit(1);
    }
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Environment.GetEnvironmentVariable("SUAVO_DEBUG") == "1"
        ? Serilog.Events.LogEventLevel.Debug
        : Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "logs", "core-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50_000_000,
        rollOnFileSizeLimit: true)
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    var startupVersion = builder.Configuration.GetSection("Agent").Get<AgentOptions>()?.Version ?? "unknown";
    Log.Information("SuavoAgent.Core starting v{Version}", startupVersion);
    builder.Services.AddWindowsService(options => options.ServiceName = "SuavoAgent.Core");
    builder.Services.AddSerilog();

    builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));

    var agentOpts = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();

    // H-1: Seal plaintext credentials with DPAPI on first run (Windows only)
    SuavoAgent.Core.Config.CredentialProtector.SealSecretsFile(
        Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
        LoggerFactory.Create(lb => lb.AddSerilog()).CreateLogger("CredentialProtector"));

    Log.Information(
        "Writeback mode: {Mode} (SQL writes {Status}) — audit receipts always generated",
        agentOpts.ReceiptOnlyMode ? "RECEIPT-ONLY" : "FULL WRITEBACK",
        agentOpts.ReceiptOnlyMode ? "DISABLED" : "ENABLED");
    if (!string.IsNullOrWhiteSpace(agentOpts.ApiKey))
    {
        var cloudClient = new SuavoCloudClient(agentOpts);
        builder.Services.AddSingleton(cloudClient);
        builder.Services.AddSingleton<IPostSigner>(cloudClient);
        builder.Services.AddSingleton<SeedClient>();
    }
    else
    {
        Log.Warning("No ApiKey configured — cloud sync disabled. Set Agent:ApiKey in appsettings.json");
    }
    builder.Services.AddSingleton(sp => new SeedApplicator(sp.GetRequiredService<AgentStateDb>()));

    builder.Services.AddHostedService<HeartbeatWorker>();

    builder.Services.AddSingleton<AgentStateDb>(sp =>
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent");
        Directory.CreateDirectory(dataDir);

        // ACL-lock ProgramData\SuavoAgent to SYSTEM, LocalService, Administrators only (HIPAA 164.312(a)(2)(iv))
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var dirInfo = new DirectoryInfo(dataDir);
                var dirSecurity = dirInfo.GetAccessControl();
                dirSecurity.SetAccessRuleProtection(true, false); // Remove inherited rules
                dirSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                dirSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.LocalServiceSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                dirSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                // NetworkService needs write for Broker logs
                dirSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.NetworkServiceSid, null),
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                dirInfo.SetAccessControl(dirSecurity);
            }
            catch (Exception ex)
            {
                if (OperatingSystem.IsWindows())
                    throw; // ACL lockdown is mandatory on Windows — HIPAA 164.312(a)(2)(iv)
                Log.Warning(ex, "ACL not available on this platform");
            }
        }

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
            if (OperatingSystem.IsWindows())
                throw; // DPAPI encryption is mandatory on Windows — unencrypted DB is HIPAA violation
            Log.Warning(ex, "DPAPI not available on this platform — state DB unencrypted");
        }

        // Migrate existing unencrypted DB to encrypted if key is available
        if (File.Exists(dbPath) && !string.IsNullOrEmpty(dbPassword))
        {
            var dbLogger = sp.GetRequiredService<ILogger<AgentStateDb>>();
            AgentStateDb.MigrateToEncrypted(dbPath, dbPassword, dbLogger);
        }

        var db = new AgentStateDb(dbPath, dbPassword);

        // Initialize per-agent HMAC salt (private, persisted, NOT the public AgentId)
        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        opts.HmacSalt = db.GetOrCreateHmacSalt("agent-audit");

        return db;
    });

    builder.Services.AddSingleton<BehavioralEventReceiver>(sp =>
        new BehavioralEventReceiver(sp.GetRequiredService<AgentStateDb>(), sessionId: "ipc"));

    // H-10: Write ephemeral pipe nonce so Broker can pass the randomised pipe name to Helper.
    // An attacker without knowledge of the nonce cannot pre-create a squatting pipe server.
    var pipeNonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
    var pipeName = $"SuavoAgent-{pipeNonce}";
    var cmdPipeName = $"SuavoAgent-cmd-{pipeNonce}";
    {
        var nonceDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SuavoAgent");
        Directory.CreateDirectory(nonceDir);
        File.WriteAllText(Path.Combine(nonceDir, "pipe.nonce"), pipeNonce);
    }

    // Pricing intelligence — Core→Helper command channel
    builder.Services.AddSingleton<IpcCommandClient>(sp =>
        new IpcCommandClient(cmdPipeName, sp.GetRequiredService<ILogger<IpcCommandClient>>()));
    builder.Services.AddSingleton<ExcelPricingReader>();
    builder.Services.AddSingleton<ExcelPricingWriter>();
    builder.Services.AddSingleton<PricingJobRunner>();

    // Tier-1 Reasoning — rule engine. Loaded from bundled Reasoning/Rules
    // alongside optional operator overrides in ProgramData. Fail-closed: a
    // malformed rule file prevents the agent from starting.
    builder.Services.AddSingleton<YamlRuleLoader>();
    builder.Services.AddSingleton<RuleEngine>(sp =>
    {
        var loader = sp.GetRequiredService<YamlRuleLoader>();
        var log = sp.GetRequiredService<ILogger<RuleEngine>>();

        var bundledDir = Path.Combine(AppContext.BaseDirectory, "Reasoning", "Rules");
        var overrideDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "rules");

        var rules = new List<SuavoAgent.Contracts.Reasoning.Rule>();
        rules.AddRange(loader.LoadFromDirectory(bundledDir));
        rules.AddRange(loader.LoadFromDirectory(overrideDir));

        var engine = new RuleEngine(rules, log);
        Log.Information("RuleEngine loaded {Count} rules across {Skills} skill(s): {SkillList}",
            engine.RuleCount, engine.KnownSkills.Count, string.Join(", ", engine.KnownSkills));
        return engine;
    });

    builder.Services.AddSingleton<IpcPipeServer>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<IpcPipeServer>>();
        var eventRateLimiter = new SuavoAgent.Core.Ipc.EventRateLimiter(maxEventsPerSecond: 500);
        return new IpcPipeServer(pipeName, msg =>
        {
            logger.LogDebug("IPC: {Command}", msg.Command);

            switch (msg.Command)
            {
                case SuavoAgent.Contracts.Ipc.IpcCommands.GetHealth:
                {
                    var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
                    var db = sp.GetRequiredService<AgentStateDb>();
                    var snapshot = new HealthSnapshot(opts, db, sp, DateTimeOffset.UtcNow);
                    var data = snapshot.Take();
                    return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                        msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.Ok, msg.Command, data, null));
                }

                case SuavoAgent.Contracts.Ipc.IpcCommands.GetPharmacySalt:
                {
                    var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
                    var db = sp.GetRequiredService<AgentStateDb>();
                    var sessionId = db.GetActiveSessionId(opts.PharmacyId ?? "");
                    var masterSalt = sessionId != null ? db.GetOrCreateHmacSalt(sessionId) : "";
                    // C-1: derive date-scoped ephemeral key — master salt never crosses the IPC boundary.
                    // Leaking the derived key can't de-anonymize data from other days.
                    string ephemeralKey = "";
                    if (masterSalt.Length > 0)
                    {
                        var dayBytes = System.Text.Encoding.UTF8.GetBytes(
                            DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));
                        ephemeralKey = Convert.ToBase64String(
                            System.Security.Cryptography.HMACSHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(masterSalt), dayBytes));
                    }
                    var saltJson = System.Text.Json.JsonSerializer.SerializeToElement(ephemeralKey);
                    return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                        msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.Ok, msg.Command, saltJson, null));
                }

                case SuavoAgent.Contracts.Ipc.IpcCommands.BehavioralEvents:
                {
                    if (!eventRateLimiter.TryAcquire())
                    {
                        logger.LogWarning("IPC: BehavioralEvents rate limit exceeded (dropped={Total})",
                            eventRateLimiter.DroppedTotal);
                        return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                            msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.BadRequest, msg.Command, null,
                            new SuavoAgent.Contracts.Ipc.IpcError("rate_limited", "rate limit exceeded", true, 0)));
                    }
                    var events = msg.Data.HasValue
                        ? System.Text.Json.JsonSerializer.Deserialize<List<SuavoAgent.Contracts.Behavioral.BehavioralEvent>>(
                            msg.Data.Value.GetRawText())
                        : null;
                    // Cap batch size at 200 to prevent memory/disk abuse
                    if (events != null && events.Count > 200)
                    {
                        var originalCount = events.Count;
                        events = events.Take(200).ToList();
                        logger.LogWarning("IPC: Capped behavioral batch from {Original} to 200 ({Dropped} dropped)",
                            originalCount, originalCount - 200);
                    }
                    if (events is { Count: > 0 })
                    {
                        var receiver = sp.GetRequiredService<BehavioralEventReceiver>();
                        receiver.ProcessBatch(events, 0);
                    }
                    return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                        msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.Ok, msg.Command, null, null));
                }

                case SuavoAgent.Contracts.Ipc.IpcCommands.SystemEvents:
                {
                    if (!eventRateLimiter.TryAcquire())
                    {
                        return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                            msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.BadRequest, msg.Command, null,
                            new SuavoAgent.Contracts.Ipc.IpcError("rate_limited", "rate limit exceeded", true, 0)));
                    }
                    var events = msg.Data.HasValue
                        ? System.Text.Json.JsonSerializer.Deserialize<List<SuavoAgent.Contracts.Behavioral.BehavioralEvent>>(
                            msg.Data.Value.GetRawText())
                        : null;
                    if (events != null && events.Count > 200)
                        events = events.Take(200).ToList();
                    if (events is { Count: > 0 })
                    {
                        var receiver = sp.GetRequiredService<BehavioralEventReceiver>();
                        receiver.ProcessBatch(events, 0);
                    }
                    return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                        msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.Ok, msg.Command, default, null));
                }

                default:
                    return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
                        msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.Ok, msg.Command, null, null));
            }
        }, logger);
    });

    builder.Services.AddSingleton<RxDetectionWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RxDetectionWorker>());
    builder.Services.AddHostedService<WritebackProcessor>();

    // Learning Agent — only active when LearningMode is enabled
    if (agentOpts.LearningMode)
    {
        builder.Services.AddHostedService<SuavoAgent.Core.Workers.LearningWorker>();
        Log.Information("Learning mode enabled — LearningWorker registered");
    }

    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

    var host = builder.Build();

    // H-1: Decrypt DPAPI-wrapped credentials for runtime use (must happen before services start)
    if (OperatingSystem.IsWindows())
    {
        var runtimeOpts = host.Services.GetRequiredService<IOptions<AgentOptions>>().Value;
        runtimeOpts.ApiKey = SuavoAgent.Core.Config.CredentialProtector.Unprotect(runtimeOpts.ApiKey);
        runtimeOpts.SqlPassword = SuavoAgent.Core.Config.CredentialProtector.Unprotect(runtimeOpts.SqlPassword);
        foreach (var ph in runtimeOpts.Pharmacies)
            ph.SqlPassword = SuavoAgent.Core.Config.CredentialProtector.Unprotect(ph.SqlPassword);
    }

    var pipeServer = host.Services.GetRequiredService<IpcPipeServer>();
    pipeServer.Start(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

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
