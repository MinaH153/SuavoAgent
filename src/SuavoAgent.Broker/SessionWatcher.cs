using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Broker;

public sealed class SessionWatcher : BackgroundService
{
    private readonly ILogger<SessionWatcher> _logger;
    private readonly IWatchdogServiceProbe _watchdogProbe;
    private readonly Func<string, bool> _fileExists;
    private readonly Dictionary<uint, HelperInfo> _helpers = new();
    private DateTimeOffset _lastAttestationWrite = DateTimeOffset.MinValue;

    // B5 — privileged-launch failure tracking. When CreateProcessAsUser keeps returning null (Broker
    // lacks SeTcbPrivilege, e.g. mis-registered as NetworkService), the old code logged CRITICAL and
    // returned — but CheckActiveSessions re-runs every 5s, so it retried FOREVER, spammed CRITICAL,
    // and never triggered repair. Now: count consecutive launch failures and, past the threshold,
    // escalate a native maintenance repair (the real fix — re-register the Broker as LocalSystem)
    // exactly once.
    // The counter moves ONLY on a launch failure, never on a Helper crash, so a persistent privilege
    // problem ("never launched") is distinguished from a Helper that launched then exited ("crashed").
    private int _consecutiveLaunchFailures;
    private bool _launchFailureEscalated;
    internal const int MaxConsecutiveLaunchFailuresBeforeEscalation = 3;

    // Fire the native SYSTEM self-uninstall host at most once (the Broker is gone seconds later anyway).
    private bool _selfUninstallLaunched;

    // Helper-restart sentinel (restart_helper command / Core self-heal): Broker-side anti-thrash —
    // even if Core misbehaves and spams sentinels, we never cycle the Helper more than once a minute.
    private DateTimeOffset _lastHelperRestartAt = DateTimeOffset.MinValue;
    internal static readonly TimeSpan MinHelperRestartSpacing = TimeSpan.FromMinutes(1);

    private record HelperInfo(
        int ProcessId,
        uint SessionId,
        DateTimeOffset LaunchedAt,
        DateTimeOffset? ProcessStartedAtUtc,
        string HelperSha256);

    public SessionWatcher(ILogger<SessionWatcher> logger)
        : this(logger, new ScWatchdogServiceProbe(), File.Exists) { }

    // Test seam: inject a fake Watchdog probe + file-existence check so the launch-failure escalation
    // is unit-testable without a live Windows session or a real maintenance host.
    internal SessionWatcher(
        ILogger<SessionWatcher> logger, IWatchdogServiceProbe watchdogProbe, Func<string, bool> fileExists)
    {
        _logger = logger;
        _watchdogProbe = watchdogProbe;
        _fileExists = fileExists;
    }

    internal int ConsecutiveLaunchFailures => _consecutiveLaunchFailures;
    internal bool LaunchFailureEscalated => _launchFailureEscalated;

    /// <summary>Past this many consecutive privileged-launch failures, stop silently retrying and
    /// escalate a native maintenance repair once. Pure so the threshold decision is unit-testable.</summary>
    internal static bool ShouldEscalateLaunchFailure(int consecutiveFailures, bool alreadyEscalated) =>
        !alreadyEscalated && consecutiveFailures >= MaxConsecutiveLaunchFailuresBeforeEscalation;

    internal void RegisterLaunchSuccess()
    {
        _consecutiveLaunchFailures = 0;
        _launchFailureEscalated = false;
    }

    /// <summary>Record a privileged-launch failure. Returns true exactly once — on the failure that
    /// first crosses the escalation threshold — so the caller invokes native repair a single time.</summary>
    internal bool RegisterLaunchFailure()
    {
        _consecutiveLaunchFailures++;
        if (ShouldEscalateLaunchFailure(_consecutiveLaunchFailures, _launchFailureEscalated))
        {
            _launchFailureEscalated = true;
            return true;
        }
        return false;
    }

    // Escalation: start the signed maintenance host beside the Broker to re-register it as
    // LocalSystem. The launch is detached because maintenance must stop Broker; synchronously
    // waiting here would create a parent/child service-stop deadlock. Internal +
    // probe/fileExists-injected so process-creation acceptance is unit-testable.
    internal bool TryStartMaintenanceRepair()
    {
        var maintenanceExecutable = _watchdogProbe.MaintenanceExecutablePath;
        if (!_fileExists(maintenanceExecutable))
        {
            _logger.LogError(
                "Cannot escalate repeated Helper-launch failure: native maintenance host not found at {Path}",
                maintenanceExecutable);
            return false;
        }

        var launchAccepted = _watchdogProbe.TryStartMaintenanceRepair(
            MaintenanceReason.HelperLaunchFailed);
        _logger.LogWarning(
            "Helper-launch native maintenance repair launch {Result}",
            launchAccepted ? "accepted" : "failed");
        return launchAccepted;
    }

    // Dashboard self-uninstall: Core persists the exact signed command plus a signed archive receipt;
    // Broker independently verifies and atomically claims it before any SYSTEM launch. A bare file,
    // malformed JSON, stale/replayed identity, or untrusted maintenance host is inert.
    private void CheckSelfUninstall()
    {
        if (_selfUninstallLaunched) return;
        var installDir = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;
        var status = SelfUninstall.TryClaimAuthenticatedRequestAndLaunch(installDir, _logger);
        if (status == SelfUninstallLaunchStatus.LaunchAccepted)
            _selfUninstallLaunched = true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session watcher started — monitoring for interactive sessions");

        // One-shot at startup: the Broker is LocalSystem, so it's the one always-on component that
        // can re-register a missing Watchdog service (Core is LocalService; the repair command needs
        // the very Watchdog that's gone). No-op on a healthy install. Best-effort, never fatal.
        EnsureWatchdogService();

        // One-shot at startup: kill any orphan Helper left by a PRIOR Broker instance before we
        // launch ours. After an OTA self-update (or a crash / Watchdog cycle) the old Helper survives
        // — it's a CreateProcessAsUser child, not terminated when its launching Broker exits — and it
        // keeps the Core<->Helper IPC pipe bound, so the fresh Helper we launch can't take the pipe
        // and Core sees `ipc_unreachable` (agent looks updated but can't render/act; the post-OTA
        // stranding on the pilot box 2026-06-01, which only a full stop+restart recovered).
        ReconcileOrphanHelpers();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckSelfUninstall();
                CheckHelperRestartRequest();
                CheckActiveSessions();
                CleanupDeadHelpers();
                RefreshHelperAttestations();
            }
            catch (Exception ex)
            {
                _logger.LogSafeWarning(ex);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private void EnsureWatchdogService()
    {
        try
        {
            var flag = Environment.GetEnvironmentVariable("SUAVO_WATCHDOG_SELF_HEAL");
            var enabled = !string.Equals(flag, "0", StringComparison.OrdinalIgnoreCase)
                          && !string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase);

            var installDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var watchdogBinary = Path.Combine(installDir, "SuavoAgent.Watchdog.exe");
            new WatchdogServiceGuard(
                _watchdogProbe, _logger, enabled, watchdogBinary)
                .EnsureWatchdogRegistered();
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
        }
    }

    // ------------------------------------------------------------------
    // restart_helper / self-heal sentinel (Core → Broker). Core cannot kill or relaunch a
    // process in the interactive session (LocalService); we can (LocalSystem) and we already
    // own the Helper lifecycle. Consume-once + freshness-bounded + Broker-side rate limit:
    //   - stale/malformed sentinel → delete WITHOUT acting (fail-closed),
    //   - honored at most once per MinHelperRestartSpacing even if Core spams requests,
    //   - the file is deleted BEFORE the kill so a crash mid-restart can never loop the kill,
    //   - relaunch happens in the SAME watch tick via the existing CheckActiveSessions path
    //     (integrity-verified CreateProcessAsUser into the active console session).
    // This is the remote lever that clears a stranded/wedged Helper command pipe without a
    // machine reboot. See HelperRestartRequest for the contract.
    // ------------------------------------------------------------------
    private void CheckHelperRestartRequest()
    {
        var path = HelperRestartRequest.DefaultPath();
        if (!_fileExists(path)) return;

        var now = DateTimeOffset.UtcNow;
        var payload = HelperRestartRequest.TryRead(path, now);
        if (payload is null)
        {
            _logger.LogWarning(
                "Helper-restart sentinel at {Path} is stale or malformed — deleting without acting (fail-closed)",
                path);
            HelperRestartRequest.TryDelete(path);
            return;
        }

        if (!ShouldHonorRestartRequest(true, _lastHelperRestartAt, now))
        {
            _logger.LogWarning(
                "Helper-restart request (reason={Reason}) IGNORED — last restart {Seconds:F0}s ago, " +
                "minimum spacing {Min}s (anti-thrash). Sentinel consumed.",
                payload.Reason, (now - _lastHelperRestartAt).TotalSeconds, MinHelperRestartSpacing.TotalSeconds);
            HelperRestartRequest.TryDelete(path);
            return;
        }

        // Consume BEFORE acting — crash-safety over delivery: a Broker crash here loses one
        // request (operator/self-heal simply re-requests) instead of replaying kills forever.
        HelperRestartRequest.TryDelete(path);
        _lastHelperRestartAt = now;

        _logger.LogWarning(
            "Helper-restart request honored (requestedBy={By}, reason={Reason}) — killing all Helper " +
            "processes; relaunch follows in this same watch tick",
            payload.RequestedBy, payload.Reason);

        var killed = KillAllHelperProcesses($"restart_request:{payload.RequestedBy}");

        try
        {
            File.WriteAllText(HelperRestartRequest.DefaultReceiptPath(), System.Text.Json.JsonSerializer.Serialize(new
            {
                completedAtUtc = DateTimeOffset.UtcNow,
                killedCount = killed,
                requestedBy = payload.RequestedBy,
                reason = payload.Reason,
            }));
        }
        catch (Exception ex)
        {
            _logger.LogSafeDebug(ex);
        }
    }

    /// <summary>Pure anti-thrash rule for honoring a restart sentinel — unit-testable.</summary>
    internal static bool ShouldHonorRestartRequest(bool payloadValid, DateTimeOffset lastRestartAt, DateTimeOffset now) =>
        payloadValid && now - lastRestartAt >= MinHelperRestartSpacing;

    /// <summary>
    /// Kills every running SuavoAgent.Helper process (tracked or stray) and clears tracking, so
    /// the single-instance command pipe is released and the next launch starts clean. Returns
    /// the number of processes killed. Best-effort, never fatal.
    /// </summary>
    private int KillAllHelperProcesses(string reason)
    {
        var killed = 0;
        try
        {
            var running = Process.GetProcessesByName("SuavoAgent.Helper");
            try
            {
                foreach (var proc in running)
                {
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(3000); // release the IPC pipe before relaunch
                        killed++;
                        _logger.LogWarning("Killed Helper PID {Pid} ({Reason})", proc.Id, reason);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogSafeWarning(ex);
                    }
                }
            }
            finally
            {
                foreach (var proc in running) proc.Dispose();
            }

            _helpers.Clear();
            PersistHelperAttestations(ReadPipeNonce());
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
        }
        return killed;
    }

    // Kill Helper processes left over from a prior Broker instance so the fresh Helper we launch
    // gets a clean IPC pipe. Best-effort, never fatal; the Broker is LocalSystem so it can terminate
    // a Helper running in the user's interactive session. Waits briefly for each to exit so the pipe
    // is released before LaunchHelper runs.
    private void ReconcileOrphanHelpers()
    {
        try
        {
            var running = Process.GetProcessesByName("SuavoAgent.Helper");
            try
            {
                var orphanPids = OrphanHelperPids(
                    running.Select(p => p.Id),
                    _helpers.Values.Select(h => h.ProcessId));

                foreach (var proc in running)
                {
                    if (!orphanPids.Contains(proc.Id)) continue;
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(3000); // let it release the IPC pipe before we launch a fresh Helper
                        _logger.LogWarning(
                            "Killed orphan Helper PID {Pid} at Broker startup (prior-instance leftover holding the IPC pipe)",
                            proc.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogSafeWarning(ex);
                    }
                }
            }
            finally
            {
                foreach (var proc in running) proc.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
        }
    }

    // The running Helper PIDs that are orphans to kill: every running Helper NOT already tracked as
    // launched by THIS Broker. At startup the tracked set is empty, so every running Helper is a
    // prior-instance orphan. Pure + testable.
    internal static HashSet<int> OrphanHelperPids(
        IEnumerable<int> runningHelperPids, IEnumerable<int> trackedHelperPids)
    {
        var tracked = new HashSet<int>(trackedHelperPids);
        return runningHelperPids.Where(pid => !tracked.Contains(pid)).ToHashSet();
    }

    /// <summary>The running Helper PIDs that are stale after a session transition: every Helper whose
    /// Windows session is NOT the active console session. Session 0 helpers (the blind-actuation
    /// failure) are stale by construction since the active console is never Session 0 here (callers
    /// pass a validated non-0xFFFFFFFF id). Pure + testable.</summary>
    internal static HashSet<int> StaleSessionHelperPids(
        IEnumerable<(int Pid, uint SessionId)> runningHelpers, uint activeSessionId) =>
        runningHelpers.Where(h => h.SessionId != activeSessionId).Select(h => h.Pid).ToHashSet();

    /// <summary>Kills every Helper (tracked or stray) running OUTSIDE the active console session and
    /// drops its tracking entry, releasing the single-instance command pipe for the active session's
    /// Helper. Windows-only (session ids are meaningless elsewhere). Best-effort, never fatal.</summary>
    private void KillStaleSessionHelpers(uint activeSessionId)
    {
        if (!OperatingSystem.IsWindows() || activeSessionId == 0) return;
        try
        {
            var running = Process.GetProcessesByName("SuavoAgent.Helper");
            try
            {
                var snapshot = new List<(int Pid, uint SessionId)>(running.Length);
                foreach (var proc in running)
                {
                    try { snapshot.Add((proc.Id, (uint)proc.SessionId)); }
                    catch { /* exited between enumerate and read — nothing to do */ }
                }

                var stale = StaleSessionHelperPids(snapshot, activeSessionId);
                if (stale.Count == 0) return;

                foreach (var proc in running)
                {
                    if (!stale.Contains(proc.Id)) continue;
                    try
                    {
                        proc.Kill();
                        proc.WaitForExit(3000); // release the IPC pipe before the active session's launch
                        _logger.LogWarning(
                            "Killed stale-session Helper PID {Pid} (was in a non-active session; active console is {Active}) " +
                            "— it was blind to the screen and could strand the command pipe",
                            proc.Id, activeSessionId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogSafeWarning(ex);
                    }
                }

                var staleTracked = _helpers.Where(kv => stale.Contains(kv.Value.ProcessId))
                    .Select(kv => kv.Key).ToList();
                foreach (var session in staleTracked) _helpers.Remove(session);
                if (staleTracked.Count > 0) PersistHelperAttestations(ReadPipeNonce());
            }
            finally
            {
                foreach (var proc in running) proc.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
        }
    }

    private void CheckActiveSessions()
    {
        var activeSessionId = GetActiveConsoleSessionId();
        if (activeSessionId == 0xFFFFFFFF)
        {
            _logger.LogDebug("No active console session");
            return;
        }

        // Root-cause fix for the session-transition strand (live box, 2026-06-11): after a
        // console-session change (CRD/RDP attach, fast user switch) this watcher launched a
        // NEW Helper for the new session but left the OLD-session Helper alive — and that
        // blind survivor still owned the single-instance command pipe, so the fresh Helper
        // could never bind it: commands stranded while the process table looked healthy.
        // A Helper outside the active console session is blind by definition (the pricing
        // pre-flight refuses it anyway) — kill it so the pipe is free for the Helper that can
        // actually see the screen. Lock screen / UAC don't change the console session id, so
        // normal overnight operation never triggers this.
        KillStaleSessionHelpers(activeSessionId);

        if (_helpers.ContainsKey(activeSessionId))
        {
            var info = _helpers[activeSessionId];
            try
            {
                var proc = Process.GetProcessById(info.ProcessId);
                if (!proc.HasExited) return; // Helper still running
                _logger.LogWarning("Helper PID {Pid} for session {Session} has exited",
                    info.ProcessId, activeSessionId);
                _helpers.Remove(activeSessionId);
            }
            catch
            {
                _helpers.Remove(activeSessionId);
            }
        }

        LaunchHelper(activeSessionId);
    }

    private bool VerifyHelperIntegrity(string helperPath) =>
        VerifyHelperIntegrityAt(
            helperPath,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent"),
            _logger,
            installedServiceMode:
                Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService());

    // H-8 tamper guard, extracted + path-injectable so the decision is unit-testable (the live
    // caller passes the real %ProgramData%\SuavoAgent dir). The on-disk Helper hash must equal the
    // binaries.manifest entry or the Broker refuses to launch the Helper.
    internal static bool VerifyHelperIntegrityAt(
        string helperPath,
        string programDataDir,
        ILogger logger,
        bool installedServiceMode = false)
    {
        var manifestPath = Path.Combine(programDataDir, "binaries.manifest");
        if (!File.Exists(manifestPath))
        {
            // #11: a missing manifest is the integrity ROOT being absent. Failing open here both
            // defeats the tamper guard (delete the manifest → the Helper launches unverified) and
            // hides it (the box looks green). A native managed install persists install-state.json;
            // running as the installed Windows service is independently authoritative during migration
            // from older installs. Either signal makes a missing integrity root fail CLOSED. Only a
            // genuine console/dev run with neither signal retains the first-boot fail-open.
            var installDir = Path.GetDirectoryName(helperPath) ?? string.Empty;
            var installStatePath = Path.Combine(
                installDir,
                MaintenanceContract.InstallStateFileName);
            var installStatePresent = File.Exists(installStatePath);
            var managed = installedServiceMode || installStatePresent;
            if (managed)
            {
                logger.LogError(
                    "binaries.manifest missing on a managed install — refusing to launch Helper (fail-closed; integrity unverifiable; serviceMode={InstalledServiceMode}, installStatePresent={InstallStatePresent})",
                    installedServiceMode,
                    installStatePresent);
                return false;
            }
            logger.LogWarning(
                "binaries.manifest not found and no managed-install marker — skipping Helper integrity check (first-boot/dev)");
            return true;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!doc.RootElement.TryGetProperty("SuavoAgent.Helper.exe", out var hashEl))
            {
                logger.LogError("Helper hash not in manifest — refusing to launch (fail-closed)");
                return false;
            }
            var expected = hashEl.GetString() ?? "";
            using var stream = File.OpenRead(helperPath);
            var actual = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Helper binary hash mismatch — refusing to launch. Expected={Expected} Actual={Actual}",
                    expected, actual);
                return false;
            }
            logger.LogDebug("Helper binary integrity verified");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogSafeError(ex);
            return false;
        }
    }

    private static string? ReadPipeNonce()
    {
        var noncePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "pipe.nonce");
        if (!File.Exists(noncePath)) return null;
        try { return File.ReadAllText(noncePath).Trim(); }
        catch { return null; }
    }

    private void PersistHelperAttestations(string? pipeNonce)
    {
        if (string.IsNullOrWhiteSpace(pipeNonce)) return;

        try
        {
            var helpers = _helpers.Values
                .Where(h => h.ProcessStartedAtUtc.HasValue)
                .Select(h => new IpcPeerAttestationEntry(
                    ProcessId: h.ProcessId,
                    SessionId: h.SessionId,
                    LaunchedAt: h.LaunchedAt,
                    ProcessStartedAtUtc: h.ProcessStartedAtUtc!.Value,
                    HelperSha256: h.HelperSha256))
                .ToArray();

            IpcPeerAttestationStore.Write(
                IpcPeerAttestationStore.GetDefaultPath(),
                pipeNonce,
                helpers,
                DateTimeOffset.UtcNow);
            _lastAttestationWrite = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
        }
    }

    private void RefreshHelperAttestations()
    {
        if (_helpers.Count == 0) return;
        if (DateTimeOffset.UtcNow - _lastAttestationWrite < TimeSpan.FromMinutes(1)) return;

        PersistHelperAttestations(ReadPipeNonce());
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // Whether the Process.Start fallback may run when the privileged session
    // launch produced no PID. Extracted + internal so the fail-closed decision is
    // unit-testable (mirrors VerifyHelperIntegrityAt). On Windows the fallback
    // would run a BLIND Session-0 Helper, so refuse; on non-Windows there is no
    // session isolation and Process.Start is the legitimate dev/test path.
    internal static bool MayUseProcessStartFallback(bool isWindows) => !isWindows;

    private void LaunchHelper(uint sessionId)
    {
        var helperPath = Path.Combine(AppContext.BaseDirectory, "SuavoAgent.Helper.exe");
        if (!File.Exists(helperPath))
        {
            _logger.LogWarning("Helper not found at {Path}", helperPath);
            return;
        }

        // H-8: Verify binary hash against install manifest before launch
        if (!VerifyHelperIntegrity(helperPath))
            return;

        try
        {
            int? pid = null;
            // H-10: Pass randomised pipe nonce so Helper connects to Core's non-guessable pipe
            var nonce = ReadPipeNonce();
            var pipeArg = nonce != null ? $" --pipe SuavoAgent-{nonce}" : "";
            var cmdPipeArg = nonce != null ? $" --cmd-pipe SuavoAgent-cmd-{nonce}" : "";
            var args = $"--session {sessionId}{pipeArg}{cmdPipeArg}";
            var helperSha256 = ComputeSha256Hex(helperPath);

            // Prefer CreateProcessAsUser on Windows — launches Helper in the user's
            // interactive session with their environment and desktop access.
            if (OperatingSystem.IsWindows())
            {
                pid = NativeProcess.LaunchInSession(sessionId, helperPath, args, _logger);
            }

            // Privileged launch produced no PID. The Process.Start fallback runs
            // the Helper in the BROKER's OWN session — on Windows that is Session
            // 0, an invisible desktop where the intent cursor, screen capture and
            // UIA all run blind while the process looks alive. Refuse it on
            // Windows (fail-closed): leave the Helper down so the cloud's
            // helper-down watch reports degraded — never ship a blind Helper.
            if (pid == null && !MayUseProcessStartFallback(OperatingSystem.IsWindows()))
            {
                var escalateNow = RegisterLaunchFailure();
                // CRITICAL on the first failure and on the escalation crossing; downgrade the in-between
                // 5s retries to Warning so a down box doesn't flood the log with identical CRITICALs.
                if (_consecutiveLaunchFailures == 1 || escalateNow)
                    _logger.LogCritical(
                        "Helper launch into interactive session {Session} FAILED (attempt {Attempt}): CreateProcessAsUser " +
                        "returned null (Broker almost certainly lacks SeTcbPrivilege — it must run as LocalSystem, not " +
                        "NetworkService/LocalService). Refusing the Session-0 fallback because a Helper there is BLIND " +
                        "(no visible cursor, screen capture, or UIA) yet looks alive. Leaving Helper DOWN so the cloud reports degraded.",
                        sessionId, _consecutiveLaunchFailures);
                else
                    _logger.LogWarning(
                        "Helper launch still failing for session {Session} (attempt {Attempt})", sessionId, _consecutiveLaunchFailures);

                if (escalateNow)
                {
                    _logger.LogCritical(
                        "Helper launch has failed {Attempt} consecutive times — escalating native maintenance repair to re-register " +
                        "the Broker as LocalSystem (root cause: missing SeTcbPrivilege).", _consecutiveLaunchFailures);
                    _ = TryStartMaintenanceRepair();
                }
                return;
            }

            // Fallback: launch in current session (dev/test, non-Windows only —
            // no interactive-session isolation there).
            if (pid == null)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = helperPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                var proc = Process.Start(psi);
                pid = proc?.Id;
                if (pid != null)
                    _logger.LogInformation("Launched Helper PID {Pid} for session {Session} (dev fallback, non-Windows)",
                        pid, sessionId);
            }

            if (pid != null)
            {
                RegisterLaunchSuccess(); // a real launch clears the privileged-launch failure counter
                DateTimeOffset? processStartedAtUtc = null;
                try
                {
                    processStartedAtUtc = new DateTimeOffset(
                        Process.GetProcessById(pid.Value).StartTime.ToUniversalTime());
                }
                catch (Exception ex)
                {
                    // Normal image-path verification can still admit this Helper,
                    // but the locked-down fallback must remain unavailable.
                    _logger.LogSafeWarning(ex);
                }
                _helpers[sessionId] = new HelperInfo(
                    pid.Value,
                    sessionId,
                    DateTimeOffset.UtcNow,
                    processStartedAtUtc,
                    helperSha256);
                PersistHelperAttestations(nonce);
            }
        }
        catch (Exception ex)
        {
            _logger.LogSafeError(ex);
        }
    }

    private void CleanupDeadHelpers()
    {
        var dead = new List<uint>();
        foreach (var (sessionId, info) in _helpers)
        {
            try
            {
                var proc = Process.GetProcessById(info.ProcessId);
                if (proc.HasExited) dead.Add(sessionId);
            }
            catch { dead.Add(sessionId); }
        }

        foreach (var id in dead)
        {
            _logger.LogInformation("Cleaning up dead Helper for session {Session}", id);
            _helpers.Remove(id);
        }

        if (dead.Count > 0)
        {
            PersistHelperAttestations(ReadPipeNonce());
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    private static uint GetActiveConsoleSessionId()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return 0; // Non-Windows fallback
        return WTSGetActiveConsoleSessionId();
    }
}
