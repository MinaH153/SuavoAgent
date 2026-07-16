using System.Diagnostics;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Security;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup;

/// <summary>
/// Registers and starts SuavoAgent Windows services using the Windows Service
/// Control utility. The signed maintenance host owns runtime repair; no script is
/// persisted or executed by this installer.
/// </summary>
internal static partial class ServiceInstaller
{
    private const string CoreServiceName = CoreServiceIdentity.ServiceName;
    private const string BrokerServiceName = "SuavoAgent.Broker";
    private const string WatchdogServiceName = "SuavoAgent.Watchdog";

    /// <summary>
    /// Full service installation pipeline: stop existing -> register -> ACL -> start -> verify.
    /// Returns true only when the complete service cohort is running. The interactive
    /// Helper is intentionally not part of this result because a locked/headless machine
    /// can legitimately have no interactive desktop session.
    /// </summary>
    public static bool InstallAndStart(string installDir, string dataDir)
    {
        // Step 1: Stop and remove any existing services (watchdog first so it
        // doesn't fight the teardown by auto-restarting Core/Broker).
        StopAndRemove(WatchdogServiceName);
        StopAndRemove(BrokerServiceName);
        StopAndRemove(CoreServiceName);

        // Step 2: Create only the two roots, then identity-pin and protect them
        // before any child creation, binary lookup, or SCM registration. A
        // preplanted reparse root therefore fails before Windows follows it to
        // create logs or registers a service binary outside the signed cohort.
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(dataDir);
        LockdownInstallDirectoryAcl(installDir);
        LockdownDataDirectoryAcl(dataDir);

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

        // Core runs under LocalService for least privilege, but all protected
        // resources authorize its unique NT SERVICE\SuavoAgent.Core SID. The
        // SID type must be enabled before any runtime ACL is applied; otherwise
        // a fresh service cannot read its own binary or write ProgramData.
        RunSc($"create {CoreServiceName} binPath= \"\\\"{corePath}\\\"\" start= delayed-auto obj= \"{CoreServiceIdentity.AccountName}\"");
        RunCmd(
            "sc.exe",
            $"sidtype \"{CoreServiceName}\" unrestricted",
            throwOnFailure: true);
        var coreSidType = RunCmd(
            "sc.exe",
            $"qsidtype \"{CoreServiceName}\"",
            throwOnFailure: true);
        if (!coreSidType.Contains("UNRESTRICTED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{CoreServiceName} did not report SERVICE_SID_TYPE_UNRESTRICTED.");
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
        // 2026-06-01. Keep every native install/repair path on this identity.
        // This is not a privilege grab —
        // the Helper itself drops to the de-privileged user token; only the
        // supervisor needs SeTcb.
        RunSc($"create {BrokerServiceName} binPath= \"\\\"{brokerPath}\\\"\" start= delayed-auto obj= \"LocalSystem\"");
        RunSc($"description {BrokerServiceName} \"Suavo pharmacy agent - session broker\"");
        RunSc($"failure {BrokerServiceName} reset= 3600 actions= restart/5000/restart/30000/restart/60000");
        RunSc($"failureflag {BrokerServiceName} 1");
        RunSc($"config {BrokerServiceName} depend= {CoreServiceName}");
        ConsoleUI.WriteOk($"{BrokerServiceName} service registered");

        // Watchdog — runs as LocalSystem (needs SCM start/query plus authority
        // to launch the fixed signed native maintenance host). No dependency on
        // Core/Broker because Watchdog must be able to restart them even when
        // they have failed. Recovery backoff is longer (10s/60s/5min) because
        // the whole point of Watchdog is that it survives churn.
        RunSc($"create {WatchdogServiceName} binPath= \"\\\"{watchdogPath}\\\"\" start= delayed-auto obj= \"LocalSystem\"");
        RunSc($"description {WatchdogServiceName} \"Suavo pharmacy agent - native process watchdog and maintenance coordinator\"");
        RunSc($"failure {WatchdogServiceName} reset= 3600 actions= restart/10000/restart/60000/restart/300000");
        RunSc($"failureflag {WatchdogServiceName} 1");
        ConsoleUI.WriteOk($"{WatchdogServiceName} service registered");

        // Step 4: Reassert both live roots now that the service SID exists,
        // then carve out the Helper's minimum. The Helper runs as the INTERACTIVE
        // user (CreateProcessAsUser — it must own the visible desktop), so a
        // SYSTEM/Admins/Core-service-SID-only DACL makes it die on its first log write
        // before it can log anything (2026-06-10 crash-loop: Broker relaunched a
        // fresh PID every 5s, zero helper logs, cloud stuck helper_attached=false).
        // The exact handle-bound policies also remove every legacy shared-service
        // ACE; no separate path-based ACL migration is permitted here.
        LockdownInstallDirectoryAcl(installDir);
        GrantInteractiveHelperExeAccess(installDir);
        LockdownDataDirectoryAcl(dataDir);
        if (!VisionRegistryProvisioner.ProvisionAndRetireLegacy(dataDir))
            throw new InvalidOperationException(
                "Vision registry authority provisioning failed.");
        GrantInteractiveHelperAccess(dataDir);
        if (!ReleaseOcrCohortProvisioner.ReassertInstalledCohortAcls(
                dataDir,
                TryLockdownVisionCohortAcl))
            throw new InvalidOperationException(
                "Reviewed vision cohort ACL reassertion failed.");

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
        if (RequiredServicesRunning(coreRunning, brokerRunning, watchdogRunning))
        {
            if (WaitForHelperProcess(TimeSpan.FromSeconds(20)))
                ConsoleUI.WriteOk("SuavoAgent.Helper is running in the interactive session");
            else
                ConsoleUI.WriteWarn(
                    "SuavoAgent.Helper has not appeared after 20s — if this session is unlocked, " +
                    "check the data-dir ACL carve-out and the broker log for a launch loop");
        }

        return RequiredServicesRunning(coreRunning, brokerRunning, watchdogRunning);
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
        // This process cleanup is required before every native reinstall/update;
        // omitting it caused the 2026-06-05 locked-binary failure.
        _ = KillCohortProcessesExceptCurrent();
    }

    /// <summary>
    /// Kills only an exact runtime executable from the MSI-owned install
    /// directory (Helper especially) after service stop. A same-named process
    /// elsewhere on the workstation is never ownership evidence.
    /// </summary>
    internal static bool KillCohortProcessesExceptCurrent()
    {
        var killedAny = false;
        var allStopped = true;
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return false; }
        var installedDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Suavo",
            "Agent");

        foreach (var p in all)
        {
            string name;
            try { name = p.ProcessName; }
            catch { p.Dispose(); continue; }

            if (p.Id != Environment.ProcessId && IsRuntimeProcessName(name))
            {
                string? executablePath;
                try { executablePath = p.MainModule?.FileName; }
                catch
                {
                    // Elevated maintenance should be able to classify its own
                    // cohort. If it cannot, do not kill by name and do not claim
                    // the quiescence proof passed.
                    allStopped = false;
                    p.Dispose();
                    continue;
                }
                if (!IsOwnedInstalledCohortProcess(
                        name,
                        executablePath,
                        installedDirectory))
                {
                    p.Dispose();
                    continue;
                }
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
                    try { if (!p.HasExited) allStopped = false; }
                    catch { }
                }
            }
            p.Dispose();
        }

        // Give the OS a beat to release the file handles before we overwrite the EXEs.
        if (killedAny) Thread.Sleep(1500);
        return allStopped;
    }

    internal static bool IsOwnedInstalledCohortProcess(
        string? processName,
        string? executablePath,
        string? installDirectory)
    {
        if (!IsRuntimeProcessName(processName) ||
            string.IsNullOrWhiteSpace(executablePath) ||
            string.IsNullOrWhiteSpace(installDirectory) ||
            executablePath.Any(char.IsControl) ||
            installDirectory.Any(char.IsControl))
            return false;
        var normalizedPath = executablePath.Trim().Trim('"').Replace('/', '\\');
        var normalizedDirectory = installDirectory.Trim().Trim('"')
            .Replace('/', '\\').TrimEnd('\\');
        if (normalizedPath.Split('\\').Any(segment => segment is "." or "..") ||
            normalizedDirectory.Split('\\').Any(segment => segment is "." or ".."))
            return false;
        return string.Equals(
            normalizedPath,
            normalizedDirectory + "\\" + processName + ".exe",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntimeProcessName(string? processName) =>
        string.Equals(processName, "SuavoAgent.Core", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "SuavoAgent.Broker", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "SuavoAgent.Watchdog", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(processName, "SuavoAgent.Helper", StringComparison.OrdinalIgnoreCase);

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

    // expectSuccess=true logs a non-zero exit. throwOnFailure=true makes a non-zero exit (or a
    // failed launch) THROW — use it for steps where silently proceeding is unsafe. Privileged ACL
    // work is intentionally absent here; it uses the native handle-bound security boundary.
    private static string RunCmd(string exe, string args, bool expectSuccess = true, bool throwOnFailure = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = TrustedWindowsSystemBinary.Resolve(exe),
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            if (expectSuccess || throwOnFailure)
                throw new InvalidOperationException($"Failed to start {exe}");
            return "";
        }

        // Start both drains before waiting. Reading either redirected stream to EOF
        // synchronously first can deadlock when the child fills the other pipe buffer,
        // which also makes the nominal timeout ineffective.
        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(30000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(5000); } catch { }
            _ = Task.WhenAll(outputTask, errorTask).Wait(TimeSpan.FromSeconds(5));
            throw new TimeoutException($"{exe} did not exit within 30 seconds");
        }

        Task.WhenAll(outputTask, errorTask).GetAwaiter().GetResult();
        var output = outputTask.Result;
        var error = errorTask.Result;

        if ((expectSuccess || throwOnFailure) && proc.ExitCode != 0)
        {
            // Never forward native stderr or arguments into the GUI, console, or
            // setup log. Windows tools can echo paths and account names supplied
            // by the environment. Exit code + fixed support code is sufficient.
            ConsoleUI.WriteInfo(
                $"A protected Windows maintenance command exited with code {proc.ExitCode}. " +
                "Support code: SETUP-NATIVE-COMMAND");
            if (throwOnFailure)
                throw new InvalidOperationException(
                    $"Protected Windows maintenance command exited with code {proc.ExitCode}.");
        }

        return output + error;
    }

    /// <summary>
    /// Pure classification logic — testable without real service probes.
    /// Core/Broker/Watchdog absent → Fail; Helper absent → Warn; all present → Ok.
    /// Helper is Warn (not Fail): a headless / locked / RDP-disconnected session legitimately has no
    /// interactive Session 1 for the Broker to spawn the Helper into — which is exactly where the
    /// console fleet-deploy installer runs. The Watchdog is not optional: without it the machine
    /// cannot satisfy the self-healing product contract and must never report installation success.
    /// </summary>
    public static GateResult ClassifyServices(bool core, bool broker, bool watchdog, bool helper)
    {
        if (!core) return new GateResult("Services", GateState.Fail, "Core service not running");
        if (!broker) return new GateResult("Services", GateState.Fail, "Broker service not running");
        if (!watchdog) return new GateResult("Services", GateState.Fail, "Watchdog service not running");
        if (!helper) return new GateResult("Services", GateState.Warn, "Helper not running yet (normal on headless/locked sessions)");
        return new GateResult("Services", GateState.Ok, "All services running");
    }

    internal static bool RequiredServicesRunning(bool core, bool broker, bool watchdog)
        => core && broker && watchdog;

    /// <summary>
    /// Live gate: probes real services and the Helper process, then delegates to ClassifyServices.
    /// </summary>
    public static GateResult ServicesRunningGate() => ClassifyServices(
        IsServiceRunning(CoreServiceName),
        IsServiceRunning(BrokerServiceName),
        IsServiceRunning(WatchdogServiceName),
        // 3s grace, not 30s: by self-verify time InstallAndStart has already waited for the Helper
        // to spawn, so it is already up on a healthy install — a long re-wait here would only delay
        // a genuine failure (and stall tests on a box without the agent).
        WaitForHelperProcess(TimeSpan.FromSeconds(3)));
}
