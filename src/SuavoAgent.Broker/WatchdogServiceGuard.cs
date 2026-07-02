using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Broker;

/// <summary>
/// Ensures the <c>SuavoAgent.Watchdog</c> service exists, breaking the install-recovery deadlock.
///
/// Failure mode this closes: an interrupted install (e.g. a locked Helper.exe on upgrade) can leave
/// Core + Broker running while the Watchdog service was never registered. Core runs as
/// <c>NT AUTHORITY\LocalService</c> and CANNOT register a service; the signed <c>repair</c> command
/// is consumed BY the Watchdog — so with no Watchdog there is no remote path to recover
/// (chicken-and-egg). The Broker runs as <c>LocalSystem</c> — the one always-on component with the
/// privilege — so it breaks the deadlock by invoking the persisted <c>bootstrap.ps1 --repair</c>,
/// which re-registers missing services against the existing binaries (proven path; not duplicated here).
///
/// Behaviour-preserving for a HEALTHY install: if the Watchdog service is already present (the common
/// case) the guard does nothing. It only acts when the service is missing AND its binary + bootstrap.ps1
/// are present (so the repair can actually succeed). Kill-switch: <c>SUAVO_WATCHDOG_SELF_HEAL=0</c>.
/// </summary>
public enum WatchdogGuardAction
{
    SkipDisabled,
    SkipNonWindows,
    SkipAlreadyInstalled,
    SkipBinaryMissing,    // OTA must deliver SuavoAgent.Watchdog.exe first
    SkipBootstrapMissing, // no bootstrap.ps1 to run --repair against
    Repair,
}

/// <summary>OS service probe + repair invoker (sc.exe / powershell). Injected for testability.</summary>
public interface IWatchdogServiceProbe
{
    /// <summary>True if the SuavoAgent.Watchdog service is registered. Fail-safe: when the state
    /// can't be determined, returns <c>true</c> so the guard does NOT trigger a false repair.</summary>
    bool IsWatchdogServiceInstalled();

    /// <summary>Invokes <c>bootstrap.ps1 --repair</c>. Returns true iff the process was launched + exited.</summary>
    bool InvokeBootstrapRepair(string bootstrapPath, TimeSpan timeout);
}

public sealed class WatchdogServiceGuard
{
    private readonly IWatchdogServiceProbe _probe;
    private readonly ILogger _log;
    private readonly bool _enabled;
    private readonly string _watchdogBinaryPath;
    private readonly string _bootstrapPath;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<bool> _isWindows;

    public WatchdogServiceGuard(
        IWatchdogServiceProbe probe,
        ILogger log,
        bool enabled,
        string watchdogBinaryPath,
        string bootstrapPath,
        Func<string, bool>? fileExists = null,
        Func<bool>? isWindows = null)
    {
        _probe = probe;
        _log = log;
        _enabled = enabled;
        _watchdogBinaryPath = watchdogBinaryPath;
        _bootstrapPath = bootstrapPath;
        _fileExists = fileExists ?? File.Exists;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    /// <summary>Pure decision — no side effects. Exhaustively unit-tested.</summary>
    public static WatchdogGuardAction Decide(
        bool enabled, bool isWindows, bool watchdogInstalled, bool watchdogBinaryExists, bool bootstrapExists)
    {
        if (!enabled) return WatchdogGuardAction.SkipDisabled;
        if (!isWindows) return WatchdogGuardAction.SkipNonWindows;
        if (watchdogInstalled) return WatchdogGuardAction.SkipAlreadyInstalled;
        if (!watchdogBinaryExists) return WatchdogGuardAction.SkipBinaryMissing;
        if (!bootstrapExists) return WatchdogGuardAction.SkipBootstrapMissing;
        return WatchdogGuardAction.Repair;
    }

    /// <summary>Run once at Broker startup. Best-effort; never throws. Returns true iff a repair ran.</summary>
    public bool EnsureWatchdogRegistered()
    {
        // Only probe the service when we'd actually act on it (Windows + enabled) — avoids spawning
        // sc.exe on a dev host and avoids a false "installed=true" masking a real decision.
        var isWindows = _isWindows();
        var installed = (_enabled && isWindows) && _probe.IsWatchdogServiceInstalled();

        var action = Decide(
            _enabled,
            isWindows,
            watchdogInstalled: installed,
            watchdogBinaryExists: _fileExists(_watchdogBinaryPath),
            bootstrapExists: _fileExists(_bootstrapPath));

        switch (action)
        {
            case WatchdogGuardAction.SkipDisabled:
                _log.LogInformation("WatchdogServiceGuard: disabled via SUAVO_WATCHDOG_SELF_HEAL");
                return false;
            case WatchdogGuardAction.SkipNonWindows:
                _log.LogDebug("WatchdogServiceGuard: non-Windows host — skipping");
                return false;
            case WatchdogGuardAction.SkipAlreadyInstalled:
                _log.LogDebug("WatchdogServiceGuard: Watchdog service present — nothing to do");
                return false;
            case WatchdogGuardAction.SkipBinaryMissing:
                _log.LogWarning(
                    "WatchdogServiceGuard: Watchdog service missing AND binary {Path} absent — OTA must deliver it first",
                    _watchdogBinaryPath);
                return false;
            case WatchdogGuardAction.SkipBootstrapMissing:
                _log.LogWarning(
                    "WatchdogServiceGuard: Watchdog service missing but bootstrap {Path} absent — cannot self-repair",
                    _bootstrapPath);
                return false;
            case WatchdogGuardAction.Repair:
                _log.LogWarning(
                    "WatchdogServiceGuard: Watchdog service MISSING — invoking bootstrap --repair to re-register (Broker is LocalSystem)");
                var ok = _probe.InvokeBootstrapRepair(_bootstrapPath, TimeSpan.FromMinutes(5));
                _log.LogWarning("WatchdogServiceGuard: bootstrap --repair {Result}", ok ? "invoked" : "failed to invoke");
                return ok;
            default:
                return false;
        }
    }
}

/// <summary>
/// Production probe: <c>sc.exe queryex</c> to detect registration (FAILED 1060 = not installed),
/// <c>powershell.exe -File bootstrap.ps1 --repair</c> to re-register. Mirrors the proven invocation
/// in <c>SuavoAgent.Watchdog.ServiceCommand</c>; kept Broker-local to avoid a cross-project dependency.
/// </summary>
public sealed class ScWatchdogServiceProbe : IWatchdogServiceProbe
{
    private const string WatchdogServiceName = "SuavoAgent.Watchdog";

    public bool IsWatchdogServiceInstalled()
    {
        var output = RunCapture("sc.exe", $"queryex \"{WatchdogServiceName}\"", TimeSpan.FromSeconds(10));
        // Fail-safe: if we can't determine state, assume installed so we do NOT trigger a false repair.
        if (output is null) return true;
        return !output.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase);
    }

    public bool InvokeBootstrapRepair(string bootstrapPath, TimeSpan timeout)
    {
        // PowerShell 5.1+ is a hard prereq for bootstrap.ps1 (enforced inside the script).
        // A launch that returns non-zero means re-registration FAILED — treating a launch as
        // success (the old `is not null` check) latches the escalation as "repaired" so it never
        // retries and the agent stays helper-down while the log claims success. Mirror the Watchdog
        // fix (ServiceCommand.RunForExitCode == 0): only exit 0 counts as a successful repair.
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{bootstrapPath}\" --repair";
        return Run("powershell.exe", args, timeout).exitCode == 0;
    }

    private static string? RunCapture(string fileName, string arguments, TimeSpan timeout) =>
        Run(fileName, arguments, timeout).output;

    /// <summary>
    /// Runs a child and returns its exit code + combined output. Stdout/stderr are drained on
    /// background reads STARTED BEFORE WaitForExit — otherwise a child that fills its ~64 KB pipe
    /// buffer (a chatty bootstrap --repair) blocks on write and never exits, wedging us for the full
    /// timeout. exitCode is a non-zero sentinel (-1) on start failure / timeout / exception.
    /// </summary>
    private static (int exitCode, string? output) Run(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (-1, null);

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, null);
            }
            // Child has exited; the drain tasks complete promptly. Bound the join so a stuck pipe
            // can't hang us past the deadline.
            var joined = Task.WhenAll(stdout, stderr).Wait(TimeSpan.FromSeconds(5));
            var output = joined ? (stdout.Result + stderr.Result) : string.Empty;
            return (p.ExitCode, output);
        }
        catch
        {
            return (-1, null);
        }
    }
}
