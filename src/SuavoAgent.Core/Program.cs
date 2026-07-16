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
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Diagnostics;
using SuavoAgent.Core.Vision;
using SuavoAgent.Core.Health;

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

// LocalService is shared by unrelated Windows services. Core may proceed only
// when SCM has added the unique NT SERVICE\SuavoAgent.Core SID configured by
// the signed installer/repair path. Keep this before log, config, credential,
// state, IPC, and network initialization so a misconfigured old install fails
// closed without touching protected runtime state.
if (OperatingSystem.IsWindows())
    CoreServiceIdentityGuard.DemandCurrentProcessHasServiceSid();

// Crash sink: before ANY other code runs, wire a last-resort handler that
// persists a PHI-safe structural failure record under ProgramData (writable by
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
    "EarlyBootstrap" => "early_bootstrap",
    "Main" => "main",
    _ => "unknown",
};
static void WriteCrash(string stage, Exception? exception)
{
    try
    {
        var line = $"[{DateTimeOffset.UtcNow:O}] code=core.{SafeCrashStage(stage)}.fatal " +
                   $"exception_type={SafeExceptionType(exception)}{Environment.NewLine}";
        File.AppendAllText(
            Path.Combine(CoreCrashDir(), "startup-crash.log"),
            line,
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
    catch { /* last resort — nothing we can do */ }
}

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    WriteCrash("UnhandledException", e.ExceptionObject as Exception);
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
        .SanitizeCoreDiagnostics()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(dataDir, "logs", "startup-.log"),
            encoding: new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7)
        .CreateLogger();

    using var earlyLogFactory = LoggerFactory.Create(lb => lb.AddSerilog(serilogLogger));
    var earlyLog = earlyLogFactory.CreateLogger("SuavoAgent.Bootstrap");

    // Core runs under LocalService but is authorized only through its exact service SID and has
    // read/execute-only access to Program Files. OTA staging happens
    // under ProgramData; the LocalSystem Watchdog and signed Maintenance coordinator own activation,
    // rollback, and service lifecycle. Never inspect or mutate legacy install-dir sentinels here.
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
    .SanitizeCoreDiagnostics()
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
    // Vision has one authority: a strict generation-numbered value under the
    // Setup-owned HKLM key. Apply it after every file/environment/config-sync
    // provider so generic overrides cannot bypass the machine consent state.
    // An absent value is an explicit default-disabled posture; malformed state
    // is a startup fault, not a silent downgrade to disabled.
    var visionDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent");
    IVisionConfigurationStore visionStore = new WindowsVisionConfigurationStore();
    var visionRegistryState = VisionConfigurationRegistry.Load(
        visionStore,
        visionDataDirectory);
    if (!visionRegistryState.IsValid)
    {
        Log.Error(
            "Vision registry state is invalid code={Code}; Core startup refused",
            visionRegistryState.Code);
        throw new InvalidDataException(
            $"Vision registry state is invalid ({visionRegistryState.Code}).");
    }
    builder.Configuration.AddInMemoryCollection(
        visionRegistryState.EffectiveOptions.ToConfigurationValues());
    // Version comes from the STAMPED ASSEMBLY, not appsettings.json — the OTA swaps the binaries but
    // never rewrites Agent.Version, so config-sourced version drifts stale after every update. See AgentVersion.
    var startupVersion = AgentVersion.Resolve(builder.Configuration.GetSection("Agent").Get<AgentOptions>()?.Version);
    Log.Information("SuavoAgent.Core starting v{Version}", startupVersion);
    builder.Services.AddWindowsService(options => options.ServiceName = "SuavoAgent.Core");
    builder.Services.AddSerilog();
    builder.Services.AddSingleton(visionStore);
    builder.Services.AddSingleton(new VisionConfigurationStatusProvider(
        visionRegistryState,
        visionStore,
        visionDataDirectory));
    builder.Services.AddSingleton(new VisionConfigurationCoordinator(
        visionStore,
        visionDataDirectory));

    builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));

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

    IEncryptedCredentialStore? credentialStore = null;
    CloudCredentialBootstrapResult? credentialBootstrap = null;

    // Cloud auth is mutable (rotation/recovery) and therefore lives only in
    // ProgramData's machine-DPAPI credential store. The protected store is
    // loaded before any cloud/HMAC client captures AgentOptions. A legacy
    // appsettings ApiKey is a one-way migration input; Core never writes to
    // Program Files and never treats appsettings as authoritative afterward.
    if (OperatingSystem.IsWindows())
    {
        credentialStore = CredentialStoreFactory.Create();
        CloudCredentialBootstrapper.ValidateSqlSecretsAreProtected(agentOpts, enforce: true);
        credentialBootstrap = CloudCredentialBootstrapper.LoadOrMigrate(
            credentialStore,
            agentOpts,
            unprotectLegacyValue: true);
        agentOpts.ApiKey = credentialBootstrap.AuthKey;
        agentOpts.InstallProvisioningId = credentialBootstrap.ProvisioningId;
        agentOpts.DeviceAttestationKeyName = credentialBootstrap.DeviceKeyName;
        agentOpts.DeviceAttestationKeyId = credentialBootstrap.DeviceKeyId;
        agentOpts.InstallDeviceCode = credentialBootstrap.DeviceCode;
        agentOpts.InstallDeviceChallenge = credentialBootstrap.DeviceChallenge;
        if (!string.IsNullOrWhiteSpace(credentialBootstrap.DeviceFingerprint) &&
            !string.Equals(
                agentOpts.MachineFingerprint,
                credentialBootstrap.DeviceFingerprint,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Pending device proof fingerprint does not match this target configuration.");
        agentOpts.SqlPassword = CredentialProtector.Unprotect(agentOpts.SqlPassword);
        foreach (var ph in agentOpts.Pharmacies)
            ph.SqlPassword = CredentialProtector.Unprotect(ph.SqlPassword);
    }

    // Every IOptions consumer gets the same credential-store override as the
    // captured local AgentOptions above. appsettings can no longer win through
    // a later bind/reload, including when a stale legacy ApiKey remains inert.
    var runtimeAuthKey = agentOpts.ApiKey;
    var runtimeInstallProvisioningId = agentOpts.InstallProvisioningId;
    var runtimeDeviceKeyName = agentOpts.DeviceAttestationKeyName;
    var runtimeDeviceKeyId = agentOpts.DeviceAttestationKeyId;
    var runtimeInstallDeviceCode = agentOpts.InstallDeviceCode;
    var runtimeInstallDeviceChallenge = agentOpts.InstallDeviceChallenge;
    builder.Services.PostConfigure<AgentOptions>(o =>
    {
        o.Version = AgentVersion.Resolve(o.Version);
        o.ApiKey = runtimeAuthKey;
        o.InstallProvisioningId = runtimeInstallProvisioningId;
        o.DeviceAttestationKeyName = runtimeDeviceKeyName;
        o.DeviceAttestationKeyId = runtimeDeviceKeyId;
        o.InstallDeviceCode = runtimeInstallDeviceCode;
        o.InstallDeviceChallenge = runtimeInstallDeviceChallenge;
        if (OperatingSystem.IsWindows())
        {
            CloudCredentialBootstrapper.ValidateSqlSecretsAreProtected(o, enforce: true);
            o.SqlPassword = CredentialProtector.Unprotect(o.SqlPassword);
            foreach (var pharmacy in o.Pharmacies)
                pharmacy.SqlPassword = CredentialProtector.Unprotect(pharmacy.SqlPassword);
        }
    });

    var isDeviceProbation =
        credentialBootstrap?.Source == CloudCredentialSource.PendingProvisioning;
    var observationIdentity =
        SuavoAgent.Contracts.Security.ObservationActivationIdentityStore.LoadProduction();
    builder.Services.AddSingleton(new SuavoAgent.Contracts.Security.ObservationActivationAuthority(
        identity: observationIdentity));

    Log.Information(
        "Writeback mode: {Mode} (SQL writes {Status}) — audit receipts always generated",
        agentOpts.ReceiptOnlyMode ? "RECEIPT-ONLY" : "FULL WRITEBACK",
        agentOpts.ReceiptOnlyMode ? "DISABLED" : "ENABLED");
    if (!string.IsNullOrWhiteSpace(agentOpts.ApiKey))
    {
        builder.Services.AddSingleton<SuavoAgent.Core.Cloud.IDeviceAuthoritySigner>(sp =>
            new SuavoAgent.Core.Cloud.DeviceAuthoritySigner(
                sp.GetRequiredService<IOptions<AgentOptions>>().Value));
        if (isDeviceProbation)
        {
            // Pending authority gets one deliberately narrow transport. It
            // cannot heartbeat, fetch commands/config, upload observations, or
            // reach any PHI-bearing route before Setup commits the device.
            builder.Services.AddSingleton(new DeviceProbationCloudClient(agentOpts));
            Log.Information(
                "Cloud credential is in device probation — only PHI-free probation health egress is registered");
        }
        else
        {
            var cloudClient = new SuavoCloudClient(agentOpts);
            builder.Services.AddSingleton(cloudClient);
            builder.Services.AddSingleton<IPostSigner>(cloudClient);
            builder.Services.AddSingleton<
                SuavoAgent.Core.Cloud.IObservationActivationRequestSigner>(sp =>
                new SuavoAgent.Core.Cloud.ObservationActivationRequestSigner(
                    observationIdentity,
                    sp.GetRequiredService<AgentStateDb>()));
            builder.Services.AddHostedService<ObservationActivationLeaseWorker>();
            builder.Services.AddHostedService<AutonomyEvidenceSyncWorker>();
            builder.Services.AddSingleton<PricingJobCloudUploader>();
            builder.Services.AddHostedService<PricingResultOutboxWorker>();
            builder.Services.AddSingleton<PricingTerminalAckOutbox>();
            builder.Services.AddHostedService<PricingTerminalAckOutboxWorker>();
            builder.Services.AddSingleton<SeedClient>();
            builder.Services.AddSingleton<IAgentCredentialRecoveryClient>(sp =>
                new AgentCredentialRecoveryClient(
                    agentOpts,
                    credentialStore!,
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
            AllowAutoRedirect = false,
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
    }
    else
    {
        Log.Warning("No protected cloud credential is available — cloud sync disabled; reconnect this workstation from the Suavo dashboard");
    }

    if (isDeviceProbation)
    {
        // The pending credential is intentionally hosted in a separate,
        // minimum-necessary process graph. No state DB, Rx detector, pricing,
        // vision, learning, IPC actuation, command poller, or PHI-capable
        // worker is constructed before PIC approval and cloud promotion.
        builder.Services.AddSingleton<IPioneerRxProbationSqlCanary, PioneerRxProbationSqlCanary>();
        builder.Services.AddHostedService<DeviceProbationWorker>();
        builder.Services.Configure<HostOptions>(options =>
            options.ShutdownTimeout = TimeSpan.FromSeconds(15));
        using var probationHost = builder.Build();
        Log.Information(
            "Device probation host started with TLS and INFORMATION_SCHEMA canary only");
        probationHost.Run();
        return;
    }

    builder.Services.AddSingleton(sp => new SeedApplicator(sp.GetRequiredService<AgentStateDb>()));

    // M3 per-task autonomy graduation ledger (fed by finished pricing/workflow runs).
    builder.Services.AddSingleton(sp => new SuavoAgent.Core.Autonomy.TaskAutonomyLedger(
        sp.GetRequiredService<AgentStateDb>(),
        sp.GetRequiredService<IOptions<AgentOptions>>().Value.TaskAutonomyCleanRunsThreshold,
        sp.GetRequiredService<IOptions<AgentOptions>>().Value,
        sp.GetService<SuavoAgent.Core.Cloud.IDeviceAuthoritySigner>()));
    builder.Services.AddSingleton<SuavoAgent.Core.Autonomy.IPioneerRxAutonomyIdentityProvider>(sp =>
        new SuavoAgent.Core.Autonomy.PioneerRxAutonomyIdentityProvider(
            sp.GetRequiredService<IOptions<AgentOptions>>().Value));

    // Adapter registry — single source of per-PMS Core config + the enforced PHI-policy invariant.
    builder.Services.AddAdapterRegistry();
    builder.Services.AddSingleton<SuavoAgent.Core.Learning.ActivePmsAdapterRegistry>();
    builder.Services.AddSingleton<SuavoAgent.Core.Learning.IActivePmsAdapterRegistry>(sp =>
        sp.GetRequiredService<SuavoAgent.Core.Learning.ActivePmsAdapterRegistry>());

    // Supervised-worker health: ResilientHostedService workers record faults here; the
    // heartbeat (HealthSnapshot.workers[]) surfaces restart-looping/escalated workers to the
    // cloud for closed-loop remediation. Singleton so every worker + the snapshot share it.
    builder.Services.AddSingleton<SuavoAgent.Core.Workers.WorkerHealthRegistry>();
    // One process-wide human-control constitution. Every Heartbeat actuation
    // path resolves this exact singleton so Pause/Stop reaches every run.
    builder.Services.AddSingleton(sp =>
    {
        var observationAuthority = sp.GetRequiredService<
            SuavoAgent.Contracts.Security.ObservationActivationAuthority>();
        var coordinator = new SuavoAgent.Core.Autonomy.AutopilotRunCoordinator(
            SuavoAgent.Contracts.Security.ObservationActivationIdentityStore.LoadProduction(),
            SuavoAgent.Contracts.Security.ObservationControlStateStore.DefaultPath(),
            revokeObservationAuthority: () => observationAuthority.RevokeLocalAuthority(),
            isObservationAuthorized: () => observationAuthority.Refresh().ObservationEnabled);
        observationAuthority.AuthorityLost += _ =>
            coordinator.EnforceObservationAuthorityLost();
        return coordinator;
    });

    builder.Services.AddHostedService<HeartbeatWorker>();
    // Liveness beacon — the Watchdog reads it to detect an alive-but-hung Core (SCM can't see deadlock).
    builder.Services.AddHostedService(sp =>
        new SuavoAgent.Core.Workers.LivenessBeaconWorker(
            sp.GetRequiredService<ILogger<SuavoAgent.Core.Workers.LivenessBeaconWorker>>()));

    // VisionCaptureWorker — fires periodic capture_screen IPC commands when
    // Vision.Enabled + PeriodicCapture.Enabled + active learning session.
    // Both gates default OFF so this is a no-op until a pilot opts in.
    builder.Services.Configure<VisionOptions>(
        builder.Configuration.GetSection("Agent:Vision"));
    builder.Services.AddSingleton<SuavoAgent.Core.Workers.VisionCaptureTelemetry>();
    builder.Services.AddSingleton<IVisionShadowReasoner, VisionGroundedShadowReasoner>();
    builder.Services.AddHostedService<SuavoAgent.Core.Workers.VisionCaptureWorker>();

    builder.Services.AddSingleton<AgentStateDb>(sp =>
    {
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent");
        Directory.CreateDirectory(dataDir);

        // Core is deliberately not privileged to repair its own filesystem
        // boundary. Setup/Maintenance establishes SYSTEM ownership and exact,
        // no-follow ACLs before services start; runtime may only verify that
        // invariant. A missing or damaged boundary is a hard startup failure,
        // never an invitation to reopen a mutable path for ACL repair.
        if (OperatingSystem.IsWindows())
        {
            if (!SuavoAgent.Core.State.InstalledDataRootVerifier.IsSafe(dataDir))
                throw new InvalidDataException(
                    "Installed data-root owner or ACL is not the exact protected policy.");
            Log.Information("core.acl_boundary_verified");
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
            Log.Warning(
                "core.dpapi_unavailable exception_type={ExceptionType}",
                SafeExceptionType(ex));
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

    // Closed-loop PioneerRx promotion: raw Rx lookup keys live only in the fixed,
    // ACL-locked ProgramData boundary as machine-DPAPI ciphertext. Candidate sync
    // fails closed if this store cannot be constructed or written.
    builder.Services.AddSingleton<IRxCorrelationStore>(_ =>
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected Rx correlation storage requires Windows DPAPI.");
        return RxCorrelationStore.CreateProduction();
    });
    builder.Services.AddSingleton<IDeliveryWritebackLedger>(_ =>
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Protected delivery writeback storage requires Windows DPAPI.");
        return DeliveryWritebackLedger.CreateProduction();
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

    CoreRuntimeServiceRegistration.Register(builder, cmdPipeName, pipeName, pipeNonce);

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

    // Learning Agent — only active when LearningMode is enabled
    if (agentOpts.LearningMode)
    {
        builder.Services.AddHostedService<SuavoAgent.Core.Workers.LearningWorker>();
        Log.Information("Learning mode enabled — LearningWorker registered");
    }

    builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));

    var host = builder.Build();

    // Persist an immutable, PHI-free migration event after state.db exists. The
    // marker was committed atomically with AuthKey, so a crash before this point
    // retries the audit on restart instead of losing evidence.
    if (credentialStore is not null && credentialBootstrap?.MigrationAuditPending == true)
    {
        var stateDb = host.Services.GetRequiredService<AgentStateDb>();
        stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: agentOpts.AgentId ?? string.Empty,
            EventType: "cloud_credential_migrated",
            FromState: "legacy_appsettings",
            ToState: "dpapi_credential_store",
            Trigger: "startup_migration",
            Actor: "system",
            SourceComponent: "cloud_credential_bootstrapper",
            CaptureReason: "move_mutable_auth_out_of_install_directory"));
        CloudCredentialBootstrapper.MarkMigrationAuditComplete(credentialStore);
        Log.Information("Cloud credential migrated to the machine-protected ProgramData store");
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
    try
    {
        Log.Fatal(
            "core.main.fatal exception_type={ExceptionType}",
            SafeExceptionType(ex));
    }
    catch { }
    WriteCrash("Main", ex);
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}
