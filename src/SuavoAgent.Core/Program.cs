using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Core;
using SuavoAgent.Core.Adapters;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Mission;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Workers;
using SuavoAgent.Diagnostics;

// Diagnostic Mesh: Wire.AttachUnhandledHooks MUST be the literal first
// executable statement (spec §7 PR 4 wire-ordering invariant; verified
// by WireOrderingTests). Wire's LocalCrashLogPath preserves the existing
// startup-crash.log file as defense-in-depth; LocalJournalPath adds the
// structured events.jsonl trail; Sentry is the BAA-covered transport
// when SUAVO_SENTRY_DSN is set.
//
// Why this comes BEFORE the AppDomain handler below: a crash during
// Wire init still leaves the legacy WriteCrash sink as a fallback, but
// once Wire is up, both fire and we get structured + plaintext audit
// of every crash from the same handler.
Wire.AttachUnhandledHooks(WireComponent.Core, new WireOptions
{
    LocalCrashLogPath = Path.Combine(CoreCrashDir(), "startup-crash.log"),
    LocalJournalPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent", "diagnostics", "events.jsonl"),
    Dsn = Environment.GetEnvironmentVariable("SUAVO_SENTRY_DSN"),
    EnableSentry = true,
});

// Crash sink: before ANY other code runs, wire a last-resort handler that
// persists unhandled exceptions to a file under ProgramData (writable by
// LocalService/NetworkService/SYSTEM). Otherwise early-bootstrap failures
// die silently under service context — the .NET runtime never gets a
// chance to emit an Application event, so operators see only Windows
// error 1067 with no underlying cause. Kept alongside Wire for
// defense-in-depth: if Wire init fails, this still captures.
//
// ProgramData is preferred over SpecialFolder.ApplicationData because
// the latter resolves to the user-scoped profile (which is empty and
// often unwritable when the service account has no loaded profile yet).
static string CoreCrashDir()
{
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var dir = Path.Combine(programData, "SuavoAgent", "logs");
    try { Directory.CreateDirectory(dir); } catch { }
    return dir;
}
static void WriteCrash(string stage, Exception ex)
{
    try
    {
        var line = $"[{DateTimeOffset.Now:O}] [{stage}] {ex.GetType().FullName}: {ex.Message}"
                   + Environment.NewLine + ex.ToString() + Environment.NewLine + Environment.NewLine;
        File.AppendAllText(
            Path.Combine(CoreCrashDir(), "startup-crash.log"),
            line,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
    catch { /* last resort — nothing we can do */ }
}

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    WriteCrash("UnhandledException", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
TaskScheduler.UnobservedTaskException += (_, e) =>
    WriteCrash("UnobservedTaskException", e.Exception);

// Bootstrap self-update — runs before any DI/config
try
{
    // Prefer ProgramData (machine-scoped, always writable by service accounts)
    // over SpecialFolder.ApplicationData which depends on a user profile.
    var dataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent");
    Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

    using var serilogLogger = new LoggerConfiguration()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(dataDir, "logs", "startup-.log"),
            encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
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

    // OTA crash-loop guard — runs BEFORE any risky init (DPAPI / DB / brain probe) so a crash-loop
    // converges to a downgrade. If the just-swapped set crash-loops past its boot budget, restore the
    // last-good .old set and restart onto it; otherwise count the boot and continue.
    var probationInstallDir = Path.GetDirectoryName(Environment.ProcessPath);
    if (!string.IsNullOrEmpty(probationInstallDir))
    {
        var probationAction = SuavoAgent.Core.Cloud.SelfUpdater.HandleProbationOnStartup(
            probationInstallDir, SuavoAgent.Core.Cloud.OtaProbationEvaluator.DefaultMaxProbationBoots, earlyLog);
        if (probationAction == SuavoAgent.Core.Cloud.SelfUpdater.ProbationStartupAction.RestartForDowngrade)
        {
            serilogLogger.Information("OTA crash-loop — downgraded to last-good binaries; restarting");
            Environment.Exit(1);
        }
    }
}
catch (Exception ex)
{
    WriteCrash("EarlyBootstrap", ex);
    throw;
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
        encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50_000_000,
        rollOnFileSizeLimit: true)
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    // When launched by SCM the process's CWD is C:\Windows\System32, not
    // the install dir, so Host.CreateApplicationBuilder's default
    // appsettings.json resolution (relative to CWD) misses our config.
    // Pin ContentRootPath to the directory of the running exe so the
    // default configuration providers find appsettings.json regardless
    // of how the process was launched.
    var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = exeDir,
    });

    // Cloud-pushed config overrides: written by ConfigSyncWorker and layered
    // on top of appsettings.json so IOptionsMonitor-aware consumers pick up
    // changes live. File is in ProgramData so it survives upgrades and is
    // ACL-locked by the state.db lockdown below.
    var configOverridesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        "config-overrides.json");
    builder.Configuration.AddJsonFile(configOverridesPath, optional: true, reloadOnChange: true);
    // Reasoning config is driven PER-BOX from ProgramData (survives OTA; appsettings.json is overwritten
    // every update) via the set_reasoning_config signed command. Shaped {"Agent":{"Reasoning":{...}}}
    // so it layers over the Agent:Reasoning section. Canary-only today; off everywhere it isn't pushed.
    var reasoningConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent",
        "reasoning.json");
    builder.Configuration.AddJsonFile(reasoningConfigPath, optional: true, reloadOnChange: true);
    var appSettingsPath = Path.Combine(exeDir, "appsettings.json");

    // Version comes from the STAMPED ASSEMBLY, not appsettings.json — the OTA swaps the binaries but
    // never rewrites Agent.Version, so config-sourced version drifts stale after every update. See AgentVersion.
    var startupVersion = AgentVersion.Resolve(builder.Configuration.GetSection("Agent").Get<AgentOptions>()?.Version);
    Log.Information("SuavoAgent.Core starting v{Version}", startupVersion);
    builder.Services.AddWindowsService(options => options.ServiceName = "SuavoAgent.Core");
    builder.Services.AddSerilog();

    builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
    // Override the config-bound Version with the stamped assembly version for every IOptions<AgentOptions>
    // consumer (heartbeat → cloud agent_version, etc.) so the reported version is OTA-correct.
    builder.Services.PostConfigure<AgentOptions>(o => o.Version = AgentVersion.Resolve(o.Version));

    var agentOpts = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
    // The local copy feeds SuavoCloudClient directly (heartbeat), so apply the same assembly-version override.
    agentOpts.Version = AgentVersion.Resolve(agentOpts.Version);

    // Actuation app-allowlist: apply operator-authorized additions from actuation.json's AllowedApps
    // so the Core-side verb pre-check (LaunchSandboxAppVerb / ClickBy* verbs) agrees with the Helper's
    // authoritative driver check. Defaults (Notepad/Calculator) always remain; a PMS box has no
    // AllowedApps and stays defaults-only. Must run before any signed run_workflow command is handled.
    SuavoAgent.Contracts.Ipc.ActuationAllowlistedSandboxApps.LoadAndExtendFromConfig();

    // Mission Loop Phase 1 — registered behind a config gate so the pilot
    // flip can open it via ConfigSyncWorker without a service restart.
    // Default is off in appsettings.json; the pilot-flip skill is the only
    // approved path to enable this against a real pharmacy.
    var missionLoopOpts =
        builder.Configuration.GetSection("MissionLoop").Get<MissionLoopOptions>()
        ?? new MissionLoopOptions();
    builder.Services.Configure<MissionLoopOptions>(
        builder.Configuration.GetSection("MissionLoop"));
    if (missionLoopOpts.Phase1.Enabled)
    {
        builder.Services.AddMissionLoopPhase1();
        Log.Information(
            "Mission Loop Phase 1 registered — config gate open (MissionLoop.Phase1.Enabled=true). Caller MUST register an IPharmacyReadAdapter before services resolve.");
    }
    else
    {
        Log.Information(
            "Mission Loop Phase 1 dormant — config gate closed (MissionLoop.Phase1.Enabled=false).");
    }

    // H-1: Seal plaintext credentials with DPAPI on first run (Windows only)
    SuavoAgent.Core.Config.CredentialProtector.SealSecretsFile(
        appSettingsPath,
        LoggerFactory.Create(lb => lb.AddSerilog()).CreateLogger("CredentialProtector"));

    // The cloud clients below capture this local AgentOptions instance, not the
    // later IOptions<AgentOptions>.Value object. On restart, appsettings.json
    // already contains DPAPI-wrapped secrets, so unwrap agentOpts before any
    // HMAC signer is constructed. Otherwise config/heartbeat clients sign with
    // the literal "DPAPI:..." envelope and cloud correctly returns 401.
    if (OperatingSystem.IsWindows())
    {
        agentOpts.ApiKey = SuavoAgent.Core.Config.CredentialProtector.Unprotect(agentOpts.ApiKey);
        agentOpts.SqlPassword = SuavoAgent.Core.Config.CredentialProtector.Unprotect(agentOpts.SqlPassword);
        foreach (var ph in agentOpts.Pharmacies)
            ph.SqlPassword = SuavoAgent.Core.Config.CredentialProtector.Unprotect(ph.SqlPassword);
    }

    Log.Information(
        "Writeback mode: {Mode} (SQL writes {Status}) — audit receipts always generated",
        agentOpts.ReceiptOnlyMode ? "RECEIPT-ONLY" : "FULL WRITEBACK",
        agentOpts.ReceiptOnlyMode ? "DISABLED" : "ENABLED");
    if (!string.IsNullOrWhiteSpace(agentOpts.ApiKey))
    {
        var cloudClient = new SuavoCloudClient(agentOpts);
        builder.Services.AddSingleton(cloudClient);
        builder.Services.AddSingleton<IPostSigner>(cloudClient);
        builder.Services.AddSingleton<PricingJobCloudUploader>();
        builder.Services.AddSingleton<SeedClient>();
        builder.Services.AddSingleton<IAgentCredentialRecoveryClient>(sp =>
            new AgentCredentialRecoveryClient(
                agentOpts,
                appSettingsPath,
                sp.GetRequiredService<ILogger<AgentCredentialRecoveryClient>>()));
        builder.Services.AddSingleton<CloudAuthRecoveryCoordinator>();

        // Cloud config-push: AgentConfigClient polls GET /api/agent/config,
        // ConfigOverrideStore flattens to config-overrides.json on disk,
        // ConfigSyncWorker runs the loop. Manual HttpClient instantiation
        // matches SuavoCloudClient's idiom — the codebase doesn't use
        // IHttpClientFactory so we don't pull in Microsoft.Extensions.Http.
        // PooledConnectionLifetime caps the DNS pin: the singleton HttpClient
        // would otherwise hold the first-resolved IP for process lifetime and
        // miss Vercel IP rotations until agent restart.
        var configHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };
        var configHttp = new HttpClient(configHandler, disposeHandler: true)
        {
            BaseAddress = new Uri(agentOpts.CloudUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };
        builder.Services.AddSingleton<IAgentConfigClient>(sp =>
            new AgentConfigClient(
                configHttp,
                agentOpts,
                sp.GetRequiredService<ILogger<AgentConfigClient>>(),
                sp.GetService<CloudAuthRecoveryCoordinator>()));
        builder.Services.AddSingleton(sp => new ConfigOverrideStore(
            configOverridesPath,
            sp.GetRequiredService<ILogger<ConfigOverrideStore>>()));
        builder.Services.AddSingleton(new ConfigSyncOptions());

        // Diagnostic Mesh OTA — wire the Phase 2 ruleset distribution path
        // (Comp 2.2). Adds IRulesetSwapper / IRulesetVerifier (eager-loads
        // the embedded trust store) / RulesetSyncStore / IRulesetClient.
        // ConfigSyncWorker auto-picks these up through its optional
        // constructor params and exposes RulesetOtaEnabled = true.
        //
        // The cloud-side `agent-ruleset` Edge Function (Comp 2A) is not
        // shipped yet — fetches will classify as Transient (404) and
        // preserve the embedded Phase 1 ruleset. Heartbeat tags
        // mesh.ruleset_swaps_total=0 give ops visibility into the
        // missing endpoint.
        builder.Services.AddDiagnosticMeshOta(agentOpts);

        builder.Services.AddHostedService<ConfigSyncWorker>();
    }
    else
    {
        Log.Warning("No ApiKey configured — cloud sync disabled. Set Agent:ApiKey in appsettings.json");
    }
    builder.Services.AddSingleton(sp => new SeedApplicator(sp.GetRequiredService<AgentStateDb>()));

    // M3 per-task autonomy graduation ledger (fed by finished pricing/workflow runs).
    builder.Services.AddSingleton(sp => new SuavoAgent.Core.Autonomy.TaskAutonomyLedger(
        sp.GetRequiredService<AgentStateDb>(),
        sp.GetRequiredService<IOptions<AgentOptions>>().Value.TaskAutonomyCleanRunsThreshold));

    // Adapter registry — single source of per-PMS Core config + the enforced PHI-policy invariant.
    builder.Services.AddAdapterRegistry();

    // Supervised-worker health: ResilientHostedService workers record faults here; the
    // heartbeat (HealthSnapshot.workers[]) surfaces restart-looping/escalated workers to the
    // cloud for closed-loop remediation. Singleton so every worker + the snapshot share it.
    builder.Services.AddSingleton<SuavoAgent.Core.Workers.WorkerHealthRegistry>();

    builder.Services.AddHostedService<HeartbeatWorker>();
    // Liveness beacon — the Watchdog reads it to detect an alive-but-hung Core (SCM can't see deadlock).
    builder.Services.AddHostedService(sp =>
        new SuavoAgent.Core.Workers.LivenessBeaconWorker(
            sp.GetRequiredService<ILogger<SuavoAgent.Core.Workers.LivenessBeaconWorker>>()));

    // VisionCaptureWorker — fires periodic capture_screen IPC commands when
    // Vision.Enabled + PeriodicCapture.Enabled + active learning session.
    // Both gates default OFF so this is a no-op until a pilot opts in.
    builder.Services.Configure<VisionOptions>(builder.Configuration.GetSection("Vision"));
    builder.Services.AddSingleton<SuavoAgent.Core.Workers.VisionCaptureTelemetry>();
    builder.Services.AddSingleton<IVisionShadowReasoner, VisionGroundedShadowReasoner>();
    builder.Services.AddHostedService<SuavoAgent.Core.Workers.VisionCaptureWorker>();

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
                // The Helper runs as a de-privileged user (always in BUILTIN\Users) and must
                // traverse the data dir (this-dir-only — NO inherited file reads: state.db is
                // plaintext PHI and state.key is machine-DPAPI). Mirrors the installer's
                // GrantInteractiveHelperAccess (BUILTIN\Users — robust vs INTERACTIVE for a
                // UAC-filtered token) so whichever lockdown runs last leaves the Helper alive.
                // (2026-06-10 helper crash-loop on fresh install.)
                dirSecurity.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    new System.Security.Principal.SecurityIdentifier(
                        System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                    System.Security.AccessControl.FileSystemRights.ReadAndExecute,
                    System.Security.AccessControl.InheritanceFlags.None,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                dirInfo.SetAccessControl(dirSecurity);

                // Helper-writable subtrees: logs\helper (Serilog + helper-crash.log)
                // and diagnostics\helper (Wire journal) get inherited Modify; the
                // logs\ and diagnostics\ roots get traverse ONLY so a local user can
                // never plant junctions where SYSTEM (Broker/Watchdog) appends —
                // log-dir EoP class. honeytokens\ is Helper-planted decoy bait.
                foreach (var (sub, rights, inherit) in new[]
                {
                    ("logs", System.Security.AccessControl.FileSystemRights.ReadAndExecute, false),
                    (Path.Combine("logs", "helper"), System.Security.AccessControl.FileSystemRights.Modify, true),
                    ("diagnostics", System.Security.AccessControl.FileSystemRights.ReadAndExecute, false),
                    (Path.Combine("diagnostics", "helper"), System.Security.AccessControl.FileSystemRights.Modify, true),
                    ("honeytokens", System.Security.AccessControl.FileSystemRights.Modify, true),
                })
                {
                    var subDir = new DirectoryInfo(Path.Combine(dataDir, sub));
                    subDir.Create();
                    var subSec = subDir.GetAccessControl();
                    subSec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                        new System.Security.Principal.SecurityIdentifier(
                            System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                        rights,
                        inherit
                            ? System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit
                            : System.Security.AccessControl.InheritanceFlags.None,
                        System.Security.AccessControl.PropagationFlags.None,
                        System.Security.AccessControl.AccessControlType.Allow));
                    subDir.SetAccessControl(subSec);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // Setting a directory's DACL requires WRITE_DAC, which the
                // NT AUTHORITY\LocalService account does not have by default.
                // The install-time bootstrap runs as Administrator and is the
                // canonical owner of this ACL — so a runtime failure here
                // just means bootstrap already locked things down and the
                // service correctly has no authority to mutate its own DACL.
                // Log it and keep going; fleet dashboard can surface the
                // signal if the bootstrap-time lockdown ever fails to run.
                Log.Warning(ex, "ACL_LOCKDOWN_DEFERRED: {Dir} DACL will be pinned by bootstrap at install time (runtime account lacks WRITE_DAC)", dataDir);
            }
            catch (Exception ex)
            {
                if (OperatingSystem.IsWindows())
                    throw; // Any non-permission ACL failure is still mandatory to surface on Windows — HIPAA 164.312(a)(2)(iv)
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

    // MOAT — durable Physarum edge-conductance store (slime-mold exploration memory). Backed by the
    // agent state.db so verified-verdict reinforcement survives restart. HeartbeatWorker feeds it after
    // every navigate run; PhysarumActionPolicy (Phase 2) will explore over it. Verified-only by
    // construction (EdgeReinforcement gates on the real PostconditionEvaluator verdict).
    builder.Services.AddSingleton<SuavoAgent.Core.Agentic.IEdgeConductanceStore>(sp =>
        new SuavoAgent.Core.State.AgentStateDbEdgeConductanceStore(sp.GetRequiredService<AgentStateDb>()));
    // Always-on slime-mold evaporation: decays unused edges toward the Floor on a 5-min tick (drift handling).
    // NOTE: the explore-only SandboxExploreSafetyGate is deliberately NOT registered here — it is constructed
    // inside HandleSandboxExploreAsync and passed as an explicit override (Codex Q3 invariant: never the
    // shared/default ISafetyGate, so live navigate_app / replay_template can't accidentally resolve it).
    builder.Services.AddHostedService<SuavoAgent.Core.Workers.PhysarumEvaporationWorker>();

    // Codex 2026-04-27 — receiver lazy-resolves the active learning session
    // per batch via AgentStateDb.GetActiveSessionId, so events arriving
    // before LearningWorker boots still land under the correct session id
    // once the worker registers a session in the DB. No more session_id
    // mismatch (Trip A) and no more pre-Configure window data loss.
    builder.Services.AddSingleton<BehavioralEventReceiver>(sp =>
    {
        var db = sp.GetRequiredService<AgentStateDb>();
        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        return new BehavioralEventReceiver(
            db,
            sessionResolver: () => db.GetActiveSessionId(opts.PharmacyId ?? string.Empty));
    });

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
    builder.Services.AddSingleton<IIpcCommandClient>(sp => sp.GetRequiredService<IpcCommandClient>());
    builder.Services.AddSingleton<IIntentCursorClient, IntentCursorClient>();
    builder.Services.AddSingleton<ExcelPricingReader>();
    builder.Services.AddSingleton<ExcelPricingWriter>();
    builder.Services.AddSingleton<IPricingLookupFactory, PioneerRxSqlPricingLookupFactory>();

    // Register both pricing executors as concrete singletons so either can be selected at
    // resolve time. The IPricingJobExecutor interface is bound below based on
    // AgentOptions.PricingExecutor — SqlFirst (default) or UiaFirst (Nadim-style UIA-only
    // pharmacies). Both implementations are fail-closed by design.
    builder.Services.AddSingleton<SqlFirstPricingJobExecutor>();
    builder.Services.AddSingleton<UiaFirstPricingJobExecutor>();
    builder.Services.AddSingleton<IPricingJobExecutor>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        IPricingJobExecutor executor = opts.PricingExecutor switch
        {
            PricingExecutorMode.UiaFirst => sp.GetRequiredService<UiaFirstPricingJobExecutor>(),
            _ => sp.GetRequiredService<SqlFirstPricingJobExecutor>(),
        };
        Log.Information(
            "Pricing executor selected: {Mode} ({Type}); throttle={ThrottleMs}ms",
            opts.PricingExecutor, executor.GetType().Name, opts.PricingThrottleMs);
        return executor;
    });

    // Autonomous daily pricing schedule — the "bot does it on its own" overnight batch (Nadim's
    // documented top-500 run). OFF by default (Agent.PricingSchedule.Enabled=false). SqlFirst-ONLY by
    // construction: it is handed the read-only SqlFirstPricingJobExecutor and additionally self-gates on
    // PricingExecutor==SqlFirst, so a timer can never drive the live PMS UI (Precedence-1).
    builder.Services.AddHostedService(sp => new SuavoAgent.Core.Workers.PricingScheduleWorker(
        sp.GetRequiredService<ILogger<SuavoAgent.Core.Workers.PricingScheduleWorker>>(),
        sp.GetRequiredService<IOptionsMonitor<AgentOptions>>(),
        sp.GetRequiredService<SqlFirstPricingJobExecutor>(),
        // Optional: present only when an API key is configured (registered in the cloud block above).
        // When present, an autonomous run surfaces in the cockpit just like a cockpit-triggered one.
        sp.GetService<SuavoAgent.Core.Cloud.PricingJobCloudUploader>()));

    // File discovery — Core side. Helper runs the actual locator; this client
    // wraps the find_file IPC call so HeartbeatWorker can dispatch
    // find_and_run_pricing_job without knowing IPC details.
    builder.Services.AddSingleton<SuavoAgent.Core.Discovery.DiscoveryClient>();

    // SP4 Phase 5.2 — Actuation chain (Track 5). Verb registry + dispatcher +
    // workflow executor. The HelperActuationGateway wraps the existing IPC
    // command client so verbs delegate to the Helper-resident
    // SendInputDriver / UiaLabelResolver. Disabled-by-default by virtue of
    // the Helper-side ActuationGate (gate flips false unless operator
    // approves via cloud + signs the per-pharmacy actuation.json).
    builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.Policy.IAuthzPolicy>(sp =>
        new SuavoAgent.Core.ActionGrammarV1.Policy.CharterDrivenAuthzPolicy());
    builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.VerbDispatcher>();
    builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.VerbRegistry>(sp =>
        SuavoAgent.Core.ActionGrammarV1.VerbRegistry.Build(
            new[] { typeof(SuavoAgent.Core.ActionGrammarV1.IVerb).Assembly },
            sp.GetRequiredService<ILogger<SuavoAgent.Core.ActionGrammarV1.VerbRegistry>>()));
    builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation.IActuationGateway>(sp =>
        new SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation.HelperActuationGateway(
            clientFactory: () => new IpcCommandClient(cmdPipeName, sp.GetRequiredService<ILogger<IpcCommandClient>>()),
            logger: sp.GetRequiredService<ILogger<SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation.HelperActuationGateway>>()));
    builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.Workflows.IWorkflowAuditClient>(sp =>
    {
        var cloud = sp.GetService<SuavoAgent.Core.Cloud.SuavoCloudClient>();
        if (cloud is null)
        {
            // Cloud is optional in some test/runtime configurations. Returning
            // a null-object lets workflows still execute locally for dry-run
            // smoke tests.
            return new SuavoAgent.Core.ActionGrammarV1.Workflows.NullWorkflowAuditClient();
        }
        return new SuavoAgent.Core.Cloud.WorkflowAuditCloudClient(
            cloud,
            sp.GetRequiredService<ILogger<SuavoAgent.Core.Cloud.WorkflowAuditCloudClient>>());
    });
    builder.Services.AddSingleton<SuavoAgent.Core.ActionGrammarV1.Workflows.WorkflowExecutor>();

    // PricingJobRunner gets an optional TieredBrain evaluator wired only when
    // Reasoning.PricingBrainEnabled is true. Default: disabled — behavior is
    // byte-for-byte identical to pre-brain. Enabling lets the brain Halt jobs
    // on streak failures (Tier-1 rules) or ambiguous states (Tier-2/3).
    builder.Services.AddSingleton<PricingJobRunner>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        var reasoning = opts.Reasoning;
        PricingBrainEvaluator? evaluator = null;
        if (reasoning.PricingBrainEnabled)
        {
            evaluator = new PricingBrainEvaluator(
                sp.GetRequiredService<TieredBrain>(),
                sp.GetRequiredService<ILogger<PricingBrainEvaluator>>());
            Log.Information(
                "Pricing brain ENABLED — PricingJobRunner will consult TieredBrain after each NDC lookup");
        }
        else
        {
            Log.Information(
                "Pricing brain disabled (Reasoning.PricingBrainEnabled=false) — runner skips TieredBrain");
        }

        return new PricingJobRunner(
            sp.GetRequiredService<ExcelPricingReader>(),
            sp.GetRequiredService<ExcelPricingWriter>(),
            sp.GetRequiredService<AgentStateDb>(),
            sp.GetRequiredService<ILogger<PricingJobRunner>>(),
            evaluator,
            interLookupDelay: TimeSpan.FromMilliseconds(opts.PricingThrottleMs));
    });

    // Tier-1 Reasoning — rule engine. The bundled catalog is embedded in this
    // assembly so it travels inside the signed single-file exe; operator
    // overrides still load from ProgramData. Fail-closed: a malformed rule
    // file prevents the agent from starting.
    builder.Services.AddSingleton<YamlRuleLoader>();
    builder.Services.AddSingleton<RuleEngine>(sp =>
    {
        var loader = sp.GetRequiredService<YamlRuleLoader>();
        var log = sp.GetRequiredService<ILogger<RuleEngine>>();

        var overrideDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "rules");

        var rules = new List<SuavoAgent.Contracts.Reasoning.Rule>();
        rules.AddRange(loader.LoadFromEmbeddedResources(
            typeof(Program).Assembly,
            "SuavoAgent.Core.Reasoning.Rules."));
        rules.AddRange(loader.LoadFromDirectory(overrideDir, required: false));

        var engine = new RuleEngine(rules, log);
        Log.Information("RuleEngine loaded {Count} rules across {Skills} skill(s): {SkillList}",
            engine.RuleCount, engine.KnownSkills.Count, string.Join(", ", engine.KnownSkills));
        return engine;
    });

    // Tier-2 Reasoning — local inference. Selected at startup based on
    // ReasoningOptions + on-disk model verification. Default: NullLocalInference
    // so the agent boots useful in rules-only mode. Real LLM wiring lands in
    // Week 2c once a signed model manifest is shipped to GitHub releases.
    builder.Services.AddSingleton<ActionVerifier>(sp =>
        new ActionVerifier(sp.GetRequiredService<IOptions<AgentOptions>>()));
    // HttpModelProvisioner auto-downloads the GGUF on first run when ModelUrl is set (so the local
    // brain ships with a client install); with no URL it behaves exactly like the legacy verify-only
    // LocalFileModelManager. Fail-soft — a download failure just leaves reasoning off.
    builder.Services.AddSingleton<IModelManager, HttpModelProvisioner>();
    // NativeLibProvisioner downloads the llama.cpp native DLLs on first run when NativeLibsUrl is set
    // (they're deliberately not shipped — stealth). Background + fail-soft, like the model provisioner.
    builder.Services.AddSingleton<NativeLibProvisioner>();
    builder.Services.AddSingleton<ILocalInference>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Reasoning;
        if (!opts.Enabled)
        {
            Log.Information("Tier-2 LocalInference disabled (Reasoning.Enabled=false) — running rules-only");
            return new NullLocalInference();
        }

        // DeferredLocalInference kicks the one-time background provisioning of the model GGUF + native
        // llama.cpp DLLs and runs rules-only until they land, then lazily constructs the real engine on
        // the next call — no second restart, no startup stall (the old factory blocked here verifying a
        // 2 GB SHA-256). Native binaries stay off the installer (stealth); they're fetched on demand.
        Log.Information("Tier-2 LocalInference ENABLED — model '{Id}' (deferred: provisioning if absent)", opts.ModelId);
        return new DeferredLocalInference(
            sp.GetRequiredService<IOptions<AgentOptions>>(),
            sp.GetRequiredService<NativeLibProvisioner>(),
            sp.GetRequiredService<IModelManager>(),
            sp.GetRequiredService<ILogger<LLamaLocalInference>>(),
            sp.GetRequiredService<ILogger<DeferredLocalInference>>());
    });

    // Tier-3 Reasoning — cloud Claude via /api/agent/reason. Opt-in via
    // Reasoning.CloudEnabled + the standard ApiKey (shared with heartbeat/sync).
    // No ApiKey means NullCloudReasoning, which TieredBrain treats as "skip Tier-3".
    builder.Services.AddSingleton<ICloudReasoning>(sp =>
    {
        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        if (!opts.Reasoning.CloudEnabled || string.IsNullOrWhiteSpace(opts.ApiKey))
        {
            Log.Information("Tier-3 CloudReasoning disabled (CloudEnabled={Enabled}, ApiKey={HasKey})",
                opts.Reasoning.CloudEnabled, !string.IsNullOrWhiteSpace(opts.ApiKey));
            return new NullCloudReasoning();
        }

        var signer = sp.GetService<IPostSigner>();
        if (signer == null)
        {
            Log.Warning("Tier-3 CloudReasoning enabled but IPostSigner not registered — disabling");
            return new NullCloudReasoning();
        }

        Log.Information("Tier-3 CloudReasoning ENABLED — will escalate low-confidence Tier-2 proposals");
        return new ClaudeCloudReasoning(signer, sp.GetRequiredService<ILogger<ClaudeCloudReasoning>>());
    });

    builder.Services.AddSingleton<TieredBrain>(sp =>
    {
        var reasoning = sp.GetRequiredService<IOptions<AgentOptions>>().Value.Reasoning;
        return new TieredBrain(
            sp.GetRequiredService<RuleEngine>(),
            sp.GetRequiredService<ILocalInference>(),
            sp.GetRequiredService<ActionVerifier>(),
            sp.GetRequiredService<ILogger<TieredBrain>>(),
            sp.GetService<ICloudReasoning>(),
            TimeSpan.FromSeconds(Math.Max(1, reasoning.InferenceTimeoutSeconds)));
    });

    builder.Services.AddSingleton<IpcPipeServer>(sp =>
    {
        var logger = sp.GetRequiredService<ILogger<IpcPipeServer>>();
        var eventRateLimiter = new SuavoAgent.Core.Ipc.EventRateLimiter(maxEventsPerSecond: 500);
        var helperAttestationPath = SuavoAgent.Contracts.Ipc.IpcPeerAttestationStore.GetDefaultPath();
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
        }, logger, isBrokerAttestedHelper: pid =>
            SuavoAgent.Contracts.Ipc.IpcPeerAttestationStore.ContainsHelper(
                helperAttestationPath,
                pipeNonce,
                pid,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(5)));
    });

    builder.Services.AddSingleton<RxDetectionWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<RxDetectionWorker>());

    // Wave 1B Track 1.4 — register the agent.health_composite emission stack so the
    // HeartbeatWorker (HeartbeatWorker.cs:92-93) actually resolves IHealthSignals +
    // HealthCompositeCalculator and emits the composite (helper/IPC/schema-canary/
    // extraction) every tick. Without this they resolved as null and the box emitted
    // ZERO composites, leaving the cloud agent-health-watch alarm blind to a silently
    // degraded box. Must come AFTER IpcPipeServer (631), RxDetectionWorker (above), and
    // AgentStateDb (286). See roadmap H2 #13.
    SuavoAgent.Core.Health.HealthCompositeServiceCollectionExtensions.AddHealthComposite(builder.Services);

    // Actuation-readiness (the strand detector). The legacy composite above reads only the
    // EVENT pipe, so a Helper whose COMMAND pipe is half-open (connected but deaf — the live-box
    // strand of 2026-06-11) still reported healthy while every pricing run failed pre-flight.
    // This stack pings the command pipe (ping-only, never actuates) on its own bounded timer,
    // mirrors the verdict into heartbeat `helper.actuation.*`, and — when the strand signature
    // persists — drops the HelperRestartRequest sentinel so the Broker relaunches the Helper
    // (same bounded self-heal the restart_helper command triggers manually).
    builder.Services.AddSingleton<SuavoAgent.Core.Health.ActuationReadinessTracker>();
    builder.Services.AddSingleton(sp => new SuavoAgent.Core.Health.ActuationReadinessProbe(
        sp.GetService<IpcCommandClient>(),
        sp.GetRequiredService<SuavoAgent.Core.Health.ActuationReadinessTracker>(),
        sp.GetRequiredService<ILogger<SuavoAgent.Core.Health.ActuationReadinessProbe>>()));
    builder.Services.AddSingleton(sp =>
    {
        var stateDb = sp.GetRequiredService<AgentStateDb>();
        var opts = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        return new SuavoAgent.Core.Health.HelperSelfHealCoordinator(
            writeSentinel: payload => SuavoAgent.Contracts.Ipc.HelperRestartRequest.Write(
                SuavoAgent.Contracts.Ipc.HelperRestartRequest.DefaultPath(), payload),
            audit: reason => stateDb.AppendChainedAuditEntry(new SuavoAgent.Core.State.AuditEntry(
                TaskId: opts.AgentId ?? "",
                EventType: "helper_restart_requested",
                FromState: "stranded",
                ToState: "restart_pending",
                Trigger: "self_heal",
                Actor: "system",
                SourceComponent: "actuation_readiness_worker",
                CaptureReason: reason)),
            logger: sp.GetRequiredService<ILogger<SuavoAgent.Core.Health.HelperSelfHealCoordinator>>());
    });
    builder.Services.AddHostedService(sp => new SuavoAgent.Core.Workers.ActuationReadinessWorker(
        sp.GetRequiredService<SuavoAgent.Core.Health.ActuationReadinessProbe>(),
        sp.GetService<SuavoAgent.Core.Health.HelperSelfHealCoordinator>(),
        sp.GetRequiredService<ILogger<SuavoAgent.Core.Workers.ActuationReadinessWorker>>(),
        sp.GetService<WorkerHealthRegistry>()));

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

    // Eager-resolve RuleEngine + TieredBrain so any startup-time config error
    // (malformed rule catalog, tampered model, etc.) crashes the host
    // immediately rather than surfacing the first time a worker calls in.
    _ = host.Services.GetRequiredService<RuleEngine>();
    var brain = host.Services.GetRequiredService<TieredBrain>();

    // Startup smoke probe — shadow-mode decision to prove the full brain
    // wiring is invocable. Logs one line so operators can confirm at a glance.
    //
    // Runs in the BACKGROUND (fire-and-forget), NOT awaited before host.Run().
    // The probe escalates the no-rule probe skill to Tier-2, which lazy-loads
    // the model and runs a grammar-constrained inference. That native llama.cpp
    // generation does NOT honor the probe's 2 s cancellation promptly (the
    // sampler can spin without yielding a cancellable point), so an *awaited*
    // probe blocked host.Run() for the full inference. A slow model (or a
    // grammar/model mismatch that produces no valid token) then exceeded the
    // SCM ~30 s start timeout -> Core never signalled SERVICE_RUNNING -> stuck
    // StartPending -> the agent could never come up with Tier-2 enabled.
    // (Live failure on Mina's box 2026-06-05.) Backgrounding it means the host
    // reaches Running immediately; the probe still logs its health line when it
    // completes. DI construction errors are already caught by the eager
    // GetRequiredService<TieredBrain>() above (which DOES crash startup).
    var probeLogger = host.Services.GetRequiredService<ILogger<Program>>();
    _ = Task.Run(() => BrainStartupProbe.RunAsync(brain, probeLogger));

    var pipeServer = host.Services.GetRequiredService<IpcPipeServer>();
    pipeServer.Start(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

    host.Run();
}
catch (Exception ex)
{
    // Log to both Serilog (for the nominal path) and the last-resort
    // crash sink (so service contexts still leave evidence even if the
    // main Serilog sink itself is the thing that failed).
    try { Log.Fatal(ex, "SuavoAgent.Core terminated unexpectedly"); } catch { }
    WriteCrash("Main", ex);
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
