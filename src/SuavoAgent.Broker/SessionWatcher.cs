using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Broker;

public sealed class SessionWatcher : BackgroundService
{
    private readonly ILogger<SessionWatcher> _logger;
    private readonly Dictionary<uint, HelperInfo> _helpers = new();
    private DateTimeOffset _lastAttestationWrite = DateTimeOffset.MinValue;

    private record HelperInfo(int ProcessId, uint SessionId, DateTimeOffset LaunchedAt, string HelperSha256);

    public SessionWatcher(ILogger<SessionWatcher> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Session watcher started — monitoring for interactive sessions");

        // One-shot at startup: the Broker is LocalSystem, so it's the one always-on component that
        // can re-register a missing Watchdog service (Core is LocalService; the repair command needs
        // the very Watchdog that's gone). No-op on a healthy install. Best-effort, never fatal.
        EnsureWatchdogService();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CheckActiveSessions();
                CleanupDeadHelpers();
                RefreshHelperAttestations();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session check error");
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
            // Fixed canonical path only — deliberately NOT honoring a SUAVO_BOOTSTRAP_PS1 override:
            // the Broker is LocalSystem and runs this script with -ExecutionPolicy Bypass, so an
            // env-pointed path would be a privileged-exec surface. The installer always persists
            // bootstrap.ps1 here (ProgramData\SuavoAgent), so the fixed path is correct.
            var bootstrap = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "bootstrap.ps1");

            new WatchdogServiceGuard(
                new ScWatchdogServiceProbe(), _logger, enabled, watchdogBinary, bootstrap)
                .EnsureWatchdogRegistered();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WatchdogServiceGuard failed (non-fatal)");
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
            _logger);

    // H-8 tamper guard, extracted + path-injectable so the decision is unit-testable (the live
    // caller passes the real %ProgramData%\SuavoAgent dir). The on-disk Helper hash must equal the
    // binaries.manifest entry or the Broker refuses to launch the Helper.
    internal static bool VerifyHelperIntegrityAt(string helperPath, string programDataDir, ILogger logger)
    {
        var manifestPath = Path.Combine(programDataDir, "binaries.manifest");
        if (!File.Exists(manifestPath))
        {
            // #11: a missing manifest is the integrity ROOT being absent. Failing open here both
            // defeats the tamper guard (delete the manifest → the Helper launches unverified) and
            // hides it (the box looks green). On a MANAGED install the installer always persists
            // bootstrap.ps1 in this same dir, so a missing manifest is anomalous → fail-CLOSED
            // (refuse; the resulting helper-down state is the degraded signal the cloud reads).
            // Only a genuine pre-install/dev box (no bootstrap.ps1) keeps the fail-open so a first
            // boot isn't bricked before install.ps1 has written the manifest.
            var managed = File.Exists(Path.Combine(programDataDir, "bootstrap.ps1"));
            if (managed)
            {
                logger.LogError(
                    "binaries.manifest missing on a managed install (bootstrap.ps1 present) — refusing to launch Helper (fail-closed; integrity unverifiable)");
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
            logger.LogError(ex, "Helper integrity check error — refusing to launch (fail-closed)");
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
                .Select(h => new IpcPeerAttestationEntry(
                    ProcessId: h.ProcessId,
                    SessionId: h.SessionId,
                    LaunchedAt: h.LaunchedAt,
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
            _logger.LogWarning(ex, "Failed to persist Helper IPC attestation");
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

            // Fallback: launch in current session (works for dev/testing)
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
                    _logger.LogInformation("Launched Helper PID {Pid} for session {Session} (fallback)",
                        pid, sessionId);
            }

            if (pid != null)
            {
                _helpers[sessionId] = new HelperInfo(
                    pid.Value,
                    sessionId,
                    DateTimeOffset.UtcNow,
                    helperSha256);
                PersistHelperAttestations(nonce);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to launch Helper for session {Session}", sessionId);
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
