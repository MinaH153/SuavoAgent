using System.Diagnostics;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup;

/// <summary>
/// Registers and starts SuavoAgent Windows services using sc.exe.
/// Mirrors the bootstrap.ps1 service registration logic.
/// </summary>
internal static class ServiceInstaller
{
    private const string CoreServiceName = "SuavoAgent.Core";
    private const string BrokerServiceName = "SuavoAgent.Broker";
    private const string WatchdogServiceName = "SuavoAgent.Watchdog";

    /// <summary>
    /// Full service installation pipeline: stop existing -> register -> ACL -> start -> verify.
    /// Returns true if Core is running. Watchdog + Broker may take a moment to settle.
    /// </summary>
    public static bool InstallAndStart(string installDir, string dataDir)
    {
        // Step 1: Stop and remove any existing services (watchdog first so it
        // doesn't fight the teardown by auto-restarting Core/Broker).
        StopAndRemove(WatchdogServiceName);
        StopAndRemove(BrokerServiceName);
        StopAndRemove(CoreServiceName);

        // Step 2: Create directories
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

        // Step 3: Register services
        var corePath = Path.Combine(installDir, "SuavoAgent.Core.exe");
        var brokerPath = Path.Combine(installDir, "SuavoAgent.Broker.exe");
        var watchdogPath = Path.Combine(installDir, "SuavoAgent.Watchdog.exe");

        if (!File.Exists(corePath))
        {
            ConsoleUI.WriteFail($"Core binary not found: {corePath}");
            return false;
        }
        if (!File.Exists(brokerPath))
        {
            ConsoleUI.WriteFail($"Broker binary not found: {brokerPath}");
            return false;
        }
        if (!File.Exists(watchdogPath))
        {
            ConsoleUI.WriteFail($"Watchdog binary not found: {watchdogPath}");
            return false;
        }

        // Core — runs as LocalService (least privilege)
        RunSc($"create {CoreServiceName} binPath= \"\\\"{corePath}\\\"\" start= delayed-auto obj= \"NT AUTHORITY\\LocalService\"");
        RunSc($"description {CoreServiceName} \"Suavo pharmacy agent - SQL polling, cloud sync\"");
        RunSc($"failure {CoreServiceName} reset= 3600 actions= restart/5000/restart/30000/restart/60000");
        RunSc($"failureflag {CoreServiceName} 1");
        ConsoleUI.WriteOk($"{CoreServiceName} service registered");

        // Broker — MUST run as LocalSystem. Its core job is the cross-session
        // launch dance: WTSQueryUserToken + DuplicateTokenEx + CreateProcessAsUser
        // to spawn the Helper inside the interactive console session (Session 1).
        // WTSQueryUserToken requires SeTcbPrivilege ("Caller must be running in
        // the LocalSystem account and must have the SE_TCB_NAME privilege" —
        // MSDN). NetworkService does NOT hold SeTcbPrivilege, so the call fails
        // 1314 (ERROR_PRIVILEGE_NOT_HELD), NativeProcess.LaunchInSession returns
        // null, and SessionWatcher silently falls back to launching the Helper in
        // the Broker's OWN session (Session 0) — an invisible desktop where the
        // intent cursor, screen capture, and UIA never render. That regression
        // (a prior "LocalSystem was excessive" edit) blinded the pilot box on
        // 2026-06-01. install.ps1 + bootstrap.ps1 already register LocalSystem;
        // this keeps the GUI/console installer in sync. Not a privilege grab —
        // the Helper itself drops to the de-privileged user token; only the
        // supervisor needs SeTcb.
        RunSc($"create {BrokerServiceName} binPath= \"\\\"{brokerPath}\\\"\" start= delayed-auto obj= \"LocalSystem\"");
        RunSc($"description {BrokerServiceName} \"Suavo pharmacy agent - session broker\"");
        RunSc($"failure {BrokerServiceName} reset= 3600 actions= restart/5000/restart/30000/restart/60000");
        RunSc($"failureflag {BrokerServiceName} 1");
        RunSc($"config {BrokerServiceName} depend= {CoreServiceName}");
        ConsoleUI.WriteOk($"{BrokerServiceName} service registered");

        // Watchdog — runs as LocalSystem (needs SCM sc.exe start/query + the
        // right to invoke bootstrap.ps1 --repair). No service dependency on
        // Core/Broker because Watchdog must be able to restart them even when
        // they have failed. Recovery backoff is longer (10s/60s/5min) because
        // the whole point of Watchdog is that it survives churn.
        RunSc($"create {WatchdogServiceName} binPath= \"\\\"{watchdogPath}\\\"\" start= delayed-auto obj= \"LocalSystem\"");
        RunSc($"description {WatchdogServiceName} \"Suavo pharmacy agent - process watchdog (auto-restarts Core/Broker, escalates to bootstrap --repair)\"");
        RunSc($"failure {WatchdogServiceName} reset= 3600 actions= restart/10000/restart/60000/restart/300000");
        RunSc($"failureflag {WatchdogServiceName} 1");
        ConsoleUI.WriteOk($"{WatchdogServiceName} service registered");

        // Step 4: Lock down data directory ACL (install dir already locked in Phase 4),
        // then carve out the Helper's minimum. The Helper runs as the INTERACTIVE
        // user (CreateProcessAsUser — it must own the visible desktop), so a
        // SYSTEM/Admins/LocalService-only DACL makes it die on its first log write
        // before it can log anything (2026-06-10 crash-loop: Broker relaunched a
        // fresh PID every 5s, zero helper logs, cloud stuck helper_attached=false).
        LockdownDirectoryAcl(dataDir);
        GrantInteractiveHelperAccess(dataDir);

        // Step 5: Start services — Core first, then Broker (depends on Core),
        // then Watchdog last so it doesn't race the fresh Core/Broker starts.
        ConsoleUI.WriteInfo("Starting services...");
        RunSc($"start {CoreServiceName}");
        Thread.Sleep(3000); // Give Core time to initialize before starting Broker
        RunSc($"start {BrokerServiceName}");
        Thread.Sleep(2000); // Let Broker settle before Watchdog starts observing
        RunSc($"start {WatchdogServiceName}");

        // Step 6: Verify
        Thread.Sleep(2000);
        var coreRunning = IsServiceRunning(CoreServiceName);
        var brokerRunning = IsServiceRunning(BrokerServiceName);
        var watchdogRunning = IsServiceRunning(WatchdogServiceName);

        if (coreRunning)
            ConsoleUI.WriteOk($"{CoreServiceName} is running");
        else
            ConsoleUI.WriteWarn($"{CoreServiceName} may not be running yet");

        if (brokerRunning)
            ConsoleUI.WriteOk($"{BrokerServiceName} is running");
        else
            ConsoleUI.WriteWarn($"{BrokerServiceName} may not be running yet");

        if (watchdogRunning)
            ConsoleUI.WriteOk($"{WatchdogServiceName} is running");
        else
            ConsoleUI.WriteWarn($"{WatchdogServiceName} may not be running yet");

        // Step 7: The Helper is the agent's hands and eyes — the Broker launches it
        // into the interactive session within seconds. Warn-grade only (a locked or
        // headless session legitimately has no Helper yet), but it catches the
        // crash-loop class on the spot instead of via cloud telemetry 10 minutes
        // later (2026-06-10: ACL lockdown killed the Helper pre-log; install still
        // said success).
        if (coreRunning && brokerRunning)
        {
            if (WaitForHelperProcess(TimeSpan.FromSeconds(20)))
                ConsoleUI.WriteOk("SuavoAgent.Helper is running in the interactive session");
            else
                ConsoleUI.WriteWarn(
                    "SuavoAgent.Helper has not appeared after 20s — if this session is unlocked, " +
                    "check the data-dir ACL carve-out and the broker log for a launch loop");
        }

        return coreRunning; // Core must be up; Watchdog will repair Broker if needed.
    }

    /// <summary>
    /// Stops all three SuavoAgent services in watchdog-first order so they
    /// cannot auto-restart each other. Safe to call when services are absent
    /// (StopAndRemove handles FAILED 1060). Call this before overwriting
    /// binaries on upgrade so the EXEs are not locked.
    /// </summary>
    public static void StopServices()
    {
        StopAndRemove(WatchdogServiceName);
        StopAndRemove(BrokerServiceName);
        StopAndRemove(CoreServiceName);
        // The Helper is a PROCESS spawned into the interactive session by the Broker — NOT a
        // Windows service — so stopping the services above does not kill it, and it keeps a file
        // lock on SuavoAgent.Helper.exe in the install dir. Without this, the very next binary
        // download fails with "the file is being used by another process" on every reinstall/update.
        // (bootstrap.ps1 has done this since Nadim's 2026-04-25 reinstall; the GUI installer did not,
        // which is exactly the failure hit on 2026-06-05.)
        KillOrphanProcesses();
    }

    /// <summary>
    /// Symmetric uninstall: stop + delete all three services (watchdog-first, kills the
    /// orphan Helper that locks binaries), then remove the data directory and the install
    /// directory. Directory deletes are best-effort with retry (a just-killed Helper can
    /// briefly hold a handle). Safe to call when services / dirs are already absent.
    /// </summary>
    public static UninstallResult Uninstall(string installDir, string dataDir)
    {
        var result = new UninstallResult();

        ConsoleUI.WriteInfo("Stopping and removing SuavoAgent services (watchdog-first)...");
        StopServices(); // watchdog -> broker -> core + KillOrphanProcesses
        result.ServicesRemoved = true;

        // Data directory: state.db, state.key, configs, logs, learned templates, audit.
        if (Directory.Exists(dataDir))
        {
            result.DataDirRemoved = SafeDeleteDirectory(dataDir);
            if (result.DataDirRemoved) ConsoleUI.WriteOk($"Removed data dir: {dataDir}");
            else ConsoleUI.WriteWarn($"Could not fully remove data dir: {dataDir}");
        }
        else { result.DataDirRemoved = true; }

        // Install directory: service binaries + appsettings.json (DPAPI-sealed credentials).
        var appsettings = Path.Combine(installDir, "appsettings.json");
        if (File.Exists(appsettings)) { try { File.Delete(appsettings); } catch { /* removed with the dir below */ } }
        if (Directory.Exists(installDir))
        {
            result.InstallDirRemoved = SafeDeleteDirectory(installDir);
            if (result.InstallDirRemoved) ConsoleUI.WriteOk($"Removed install dir: {installDir}");
            else ConsoleUI.WriteWarn($"Could not fully remove install dir: {installDir} (a file may still be locked)");
        }
        else { result.InstallDirRemoved = true; }

        // Defensive registry cleanup (the installer does not normally create these, but
        // remove them if a prior/older build did). Services were already removed above.
        TryDeleteRegistryKeyTree(@"SOFTWARE\SuavoAgent");

        result.ServicesRemaining = CountRemainingServices();
        return result;
    }

    /// <summary>Outcome of <see cref="Uninstall"/>, used for zero-residue verification.</summary>
    public sealed class UninstallResult
    {
        public bool ServicesRemoved { get; set; }
        public bool DataDirRemoved { get; set; }
        public bool InstallDirRemoved { get; set; }
        public int ServicesRemaining { get; set; }
        public bool FullyClean => ServicesRemaining == 0 && DataDirRemoved && InstallDirRemoved;
    }

    // Recursive delete that tolerates a briefly-held handle from a just-killed process.
    private static bool SafeDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                try { ClearReadOnlyRecursive(path); } catch { /* best effort */ }
                Thread.Sleep(1000);
            }
            catch { break; }
        }
        return !Directory.Exists(path);
    }

    private static void ClearReadOnlyRecursive(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { var fi = new FileInfo(file); if (fi.IsReadOnly) fi.IsReadOnly = false; } catch { }
        }
    }

    private static int CountRemainingServices()
    {
        var remaining = 0;
        foreach (var name in new[] { CoreServiceName, BrokerServiceName, WatchdogServiceName })
            if (IsServicePresent(name)) remaining++;
        return remaining;
    }

    // Present = sc.exe query did NOT return "FAILED 1060" (the service-does-not-exist code).
    private static bool IsServicePresent(string serviceName)
    {
        try
        {
            var outp = RunSc($"query {serviceName}", expectSuccess: false);
            return !outp.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void TryDeleteRegistryKeyTree(string subKey)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
        catch { /* absent or insufficient rights — services already removed by StopServices */ }
    }

    /// <summary>
    /// Kills any lingering SuavoAgent.* process (Helper especially) that survives a service stop,
    /// so the binaries can be overwritten. Best-effort: a process that already exited or that we
    /// can't touch is skipped. Waits briefly for the OS to release the file handles afterward.
    /// </summary>
    private static void KillOrphanProcesses()
    {
        var killedAny = false;
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return; }

        foreach (var p in all)
        {
            string name;
            try { name = p.ProcessName; }
            catch { p.Dispose(); continue; }

            // Process names have no ".exe" suffix; match "SuavoAgent.*" (Helper/Core/Broker/Watchdog).
            if (name.StartsWith("SuavoAgent.", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ConsoleUI.WriteInfo($"Killing lingering process {name} (PID {p.Id}) holding a binary lock...");
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(5000);
                    killedAny = true;
                }
                catch
                {
                    // Already gone, or insufficient rights — the download retry/verify will surface it.
                }
            }
            p.Dispose();
        }

        // Give the OS a beat to release the file handles before we overwrite the EXEs.
        if (killedAny) Thread.Sleep(1500);
    }

    private static void StopAndRemove(string serviceName)
    {
        try
        {
            // Check if service exists
            var queryResult = RunSc($"query {serviceName}", expectSuccess: false);
            if (queryResult.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleUI.WriteInfo($"Stopping existing {serviceName}...");
                RunSc($"stop {serviceName}", expectSuccess: false);
                Thread.Sleep(2000);
            }

            if (!queryResult.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleUI.WriteInfo($"Removing existing {serviceName}...");
                RunSc($"delete {serviceName}", expectSuccess: false);
                Thread.Sleep(1000);
            }
        }
        catch
        {
            // Service may not exist — that's fine
        }
    }

    /// <summary>
    /// Lock down directory with ACL: Admin + SYSTEM full, LocalService modify.
    /// Uses icacls.exe instead of .NET ACL API for simpler cross-compile compatibility.
    /// Public so Program.cs can call it before writing config (C-4: ACL-first).
    /// </summary>
    public static void LockdownDirectoryAcl(string path)
    {
        try
        {
            // Reset inheritance, remove all existing ACEs
            RunCmd("icacls", $"\"{path}\" /inheritance:r /grant:r \"BUILTIN\\Administrators:(OI)(CI)F\" /grant:r \"NT AUTHORITY\\SYSTEM:(OI)(CI)F\" /grant:r \"NT AUTHORITY\\LOCAL SERVICE:(OI)(CI)M\"");
            ConsoleUI.WriteOk($"ACL locked down: {path}");
        }
        catch (Exception ex)
        {
            ConsoleUI.WriteWarn($"ACL lockdown failed: {ex.Message}");
        }
    }

    // The de-privileged Helper runs as the interactive user (CreateProcessAsUser). Its token is
    // ALWAYS a member of BUILTIN\Users (S-1-5-32-545) regardless of UAC token-filtering or logon
    // type — unlike the INTERACTIVE group (S-1-5-4), which a filtered/edge-case token can lack.
    // bootstrap.ps1 (the proven install path) grants Users:RX for exactly this reason, so we use
    // the same principal. SID form (*S-1-5-32-545) = locale-independent (icacls won't mis-resolve
    // "Users" on a non-English box).
    // BUILTIN\Users — single source of truth lives in SuavoAgent.Diagnostics so the installer
    // (install-time grant) and the LocalSystem Watchdog (post-OTA re-grant) can never diverge.
    private const string HelperPrincipal = SuavoAgent.Diagnostics.HelperExeAclGrant.HelperPrincipal;

    /// <summary>
    /// Install-dir read carve-out, applied AFTER LockdownDirectoryAcl pins the install dir to
    /// Admins/SYSTEM/LocalService. SuavoAgent.Helper.exe is a COMPRESSED single-file
    /// self-extracting apphost (publish: --self-contained PublishSingleFile + EnableCompression);
    /// at startup it RE-OPENS and self-extracts its OWN exe as the running (de-privileged) user.
    /// Without read access that open is denied → the Helper dies BEFORE its first log line and the
    /// Broker churns a fresh PID ~every 5s (helper_attached=false forever; root cause 2026-06-10,
    /// confirmed on box; bootstrap.ps1 already grants this, the GUI/console installers did not).
    /// Scoped to traverse-on-dir + RX-on-Helper.exe so appsettings.json (ApiKey + SQL creds) and
    /// the other service binaries stay UNREADABLE by local users.
    /// </summary>
    public static void GrantInteractiveHelperExeAccess(string installDir)
    {
        try
        {
            // Delegate to the shared, idempotent grant so the install-time path and the LocalSystem
            // Watchdog's post-OTA re-grant are LITERALLY the same code (traverse-on-dir + RX-on-Helper.exe).
            if (SuavoAgent.Diagnostics.HelperExeAclGrant.Apply(installDir, ConsoleUI.WriteInfo))
                ConsoleUI.WriteOk("Helper apphost readable by the interactive user (single-file self-extract); appsettings stays protected");
            else
                ConsoleUI.WriteWarn("Helper apphost read-grant did NOT fully succeed — the Helper may churn (cannot self-extract)");
        }
        catch (Exception ex)
        {
            ConsoleUI.WriteWarn($"Helper apphost read-grant FAILED: {ex.Message} — the Helper will churn (cannot self-extract)");
        }
    }

    // The Helper's DATA-dir least-privilege carve-out, applied AFTER LockdownDirectoryAcl.
    // Deliberately NOT an inherited read on the data-dir root: state.db is plaintext
    // SQLite (PHI on a PMS box) and state.key is machine-scope DPAPI (any local
    // reader could decrypt), and SQLite recreates -wal/-shm constantly so even a
    // strip-after-grant would reopen a read window on every checkpoint. So:
    //   root            -> Users (RX), THIS DIR ONLY (traverse + list, no file reads)
    //   logs\helper\, diagnostics\helper\ -> Users (OI)(CI)M — the ONLY
    //     user-writable log/journal dirs. The logs\ and diagnostics\ roots stay
    //     service-only (traverse) so a local user can never plant junctions or
    //     links where SYSTEM (Broker/Watchdog) appends — log-dir EoP class
    //     (Codex review 2026-06-10 Q2).
    //   honeytokens\    -> Users (OI)(CI)M (decoy bait — user-touchable by design)
    //   vision.json / actuation.json / pioneerrx.json / honeytoken-attribution.json
    //     -> Users (R) per-file when present (flows that create or atomically
    //     rewrite these later must re-grant — replace drops per-file ACEs).
    public static void GrantInteractiveHelperAccess(string dataDir)
    {
        try
        {
            foreach (var args in BuildInteractiveGrantArgs(dataDir))
            {
                var (target, grant, ensureDir) = args;
                if (ensureDir)
                    Directory.CreateDirectory(target);
                else if (!File.Exists(target))
                    continue;
                RunCmd("icacls", $"\"{target}\" /grant \"{grant}\"");
            }
            ConsoleUI.WriteOk("Helper (interactive user) data-dir carve-out applied: traverse + logs/diagnostics write + config reads");
        }
        catch (Exception ex)
        {
            ConsoleUI.WriteWarn($"Helper data-dir carve-out failed: {ex.Message} — the Helper cannot log or read configs until this is fixed");
        }
    }

    /// <summary>Pure builder so the exact icacls grants are unit-testable.
    /// Tuple: (target path, grant spec, target-is-directory-to-create).</summary>
    internal static IReadOnlyList<(string Target, string Grant, bool EnsureDir)> BuildInteractiveGrantArgs(string dataDir) =>
    [
        (dataDir, $"{HelperPrincipal}:(RX)", true),
        (Path.Combine(dataDir, "logs"), $"{HelperPrincipal}:(RX)", true),
        (Path.Combine(dataDir, "logs", "helper"), $"{HelperPrincipal}:(OI)(CI)(M)", true),
        (Path.Combine(dataDir, "diagnostics"), $"{HelperPrincipal}:(RX)", true),
        (Path.Combine(dataDir, "diagnostics", "helper"), $"{HelperPrincipal}:(OI)(CI)(M)", true),
        // Honeytokens are decoy bait the Helper plants and watches — user-touchable
        // is their entire purpose; no SYSTEM process writes files here.
        (Path.Combine(dataDir, "honeytokens"), $"{HelperPrincipal}:(OI)(CI)(M)", true),
        (Path.Combine(dataDir, "vision.json"), $"{HelperPrincipal}:(R)", false),
        (Path.Combine(dataDir, "actuation.json"), $"{HelperPrincipal}:(R)", false),
        (Path.Combine(dataDir, "pioneerrx.json"), $"{HelperPrincipal}:(R)", false),
        (Path.Combine(dataDir, "honeytoken-attribution.json"), $"{HelperPrincipal}:(R)", false),
    ];

    /// <summary>Polls for the Helper process appearing in the interactive session.</summary>
    private static bool WaitForHelperProcess(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("SuavoAgent.Helper").Length > 0)
                    return true;
            }
            catch
            {
                // Process enumeration hiccup — keep polling until the deadline.
            }
            Thread.Sleep(2000);
        }
        return false;
    }

    private static bool IsServiceRunning(string serviceName)
    {
        try
        {
            var output = RunSc($"query {serviceName}", expectSuccess: false);
            return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string RunSc(string args, bool expectSuccess = true)
    {
        return RunCmd("sc.exe", args, expectSuccess);
    }

    private static string RunCmd(string exe, string args, bool expectSuccess = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            if (expectSuccess)
                throw new InvalidOperationException($"Failed to start {exe}");
            return "";
        }

        var output = proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);

        if (expectSuccess && proc.ExitCode != 0)
        {
            ConsoleUI.WriteInfo($"{exe} {args} -> exit {proc.ExitCode}: {error.Trim()}");
        }

        return output + error;
    }

    /// <summary>
    /// Pure classification logic — testable without real service probes.
    /// Core/Broker/Helper absent → Fail; Watchdog absent → Warn; all present → Ok.
    /// </summary>
    public static GateResult ClassifyServices(bool core, bool broker, bool watchdog, bool helper)
    {
        if (!core) return new GateResult("Services", GateState.Fail, "Core service not running");
        if (!broker) return new GateResult("Services", GateState.Fail, "Broker service not running");
        if (!helper) return new GateResult("Services", GateState.Fail, "Helper process not running");
        if (!watchdog) return new GateResult("Services", GateState.Warn, "Watchdog not running yet");
        return new GateResult("Services", GateState.Ok, "All services running");
    }

    /// <summary>
    /// Live gate: probes real services and the Helper process, then delegates to ClassifyServices.
    /// </summary>
    public static GateResult ServicesRunningGate() => ClassifyServices(
        IsServiceRunning(CoreServiceName),
        IsServiceRunning(BrokerServiceName),
        IsServiceRunning(WatchdogServiceName),
        WaitForHelperProcess(TimeSpan.FromSeconds(30)));
}
