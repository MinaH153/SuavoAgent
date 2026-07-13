using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;

namespace SuavoAgent.Broker;

/// <summary>
/// Ensures the <c>SuavoAgent.Watchdog</c> service exists, breaking the install-recovery deadlock.
///
/// Failure mode this closes: an interrupted install (e.g. a locked Helper.exe on upgrade) can leave
/// Core + Broker running while the Watchdog service was never registered. Core runs as
/// <c>NT AUTHORITY\LocalService</c> and CANNOT register a service; the signed <c>repair</c> command
/// is consumed BY the Watchdog — so with no Watchdog there is no remote path to recover
/// (chicken-and-egg). The Broker runs as <c>LocalSystem</c> — the one always-on component with the
/// privilege — so it breaks the deadlock by invoking the signed maintenance executable staged beside
/// the Broker. The maintenance host re-registers missing services against the existing binaries.
///
/// Behaviour-preserving for a HEALTHY install: if the Watchdog service is already present (the common
/// case) the guard does nothing. It only acts when the service is missing AND its binary + the native
/// maintenance host are present (so the repair can actually succeed). Kill-switch:
/// <c>SUAVO_WATCHDOG_SELF_HEAL=0</c>.
/// </summary>
public enum WatchdogGuardAction
{
    SkipDisabled,
    SkipNonWindows,
    SkipAlreadyInstalled,
    SkipBinaryMissing,      // OTA must deliver SuavoAgent.Watchdog.exe first
    SkipMaintenanceMissing, // native maintenance host is required for privileged repair
    Repair,
}

/// <summary>OS service probe + native maintenance invoker. Injected for testability.</summary>
public interface IWatchdogServiceProbe
{
    /// <summary>The fixed maintenance executable staged beside the running Broker.</summary>
    string MaintenanceExecutablePath { get; }

    /// <summary>True if the SuavoAgent.Watchdog service is registered. Fail-safe: when the state
    /// can't be determined, returns <c>true</c> so the guard does NOT trigger a false repair.</summary>
    bool IsWatchdogServiceInstalled();

    /// <summary>
    /// Starts the native maintenance host detached for a closed-set repair reason.
    /// A <c>true</c> result means only that Windows accepted process creation; the
    /// maintenance result is deliberately not awaited because repair stops Broker.
    /// </summary>
    bool TryStartMaintenanceRepair(MaintenanceReason reason);
}

public sealed class WatchdogServiceGuard
{
    private readonly IWatchdogServiceProbe _probe;
    private readonly ILogger _log;
    private readonly bool _enabled;
    private readonly string _watchdogBinaryPath;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<bool> _isWindows;

    public WatchdogServiceGuard(
        IWatchdogServiceProbe probe,
        ILogger log,
        bool enabled,
        string watchdogBinaryPath,
        Func<string, bool>? fileExists = null,
        Func<bool>? isWindows = null)
    {
        _probe = probe;
        _log = log;
        _enabled = enabled;
        _watchdogBinaryPath = watchdogBinaryPath;
        _fileExists = fileExists ?? File.Exists;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    /// <summary>Pure decision — no side effects. Exhaustively unit-tested.</summary>
    public static WatchdogGuardAction Decide(
        bool enabled, bool isWindows, bool watchdogInstalled, bool watchdogBinaryExists,
        bool maintenanceExecutableExists)
    {
        if (!enabled) return WatchdogGuardAction.SkipDisabled;
        if (!isWindows) return WatchdogGuardAction.SkipNonWindows;
        if (watchdogInstalled) return WatchdogGuardAction.SkipAlreadyInstalled;
        if (!watchdogBinaryExists) return WatchdogGuardAction.SkipBinaryMissing;
        if (!maintenanceExecutableExists) return WatchdogGuardAction.SkipMaintenanceMissing;
        return WatchdogGuardAction.Repair;
    }

    /// <summary>
    /// Run once at Broker startup. Best-effort; never throws. Returns true iff the
    /// detached maintenance process was accepted for launch, not iff repair completed.
    /// </summary>
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
            maintenanceExecutableExists: _fileExists(_probe.MaintenanceExecutablePath));

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
            case WatchdogGuardAction.SkipMaintenanceMissing:
                _log.LogWarning(
                    "WatchdogServiceGuard: Watchdog service missing but native maintenance host {Path} absent — cannot self-repair",
                    _probe.MaintenanceExecutablePath);
                return false;
            case WatchdogGuardAction.Repair:
                _log.LogWarning(
                    "WatchdogServiceGuard: Watchdog service MISSING — starting detached native maintenance repair (Broker is LocalSystem)");
                var launchAccepted = _probe.TryStartMaintenanceRepair(
                    MaintenanceReason.WatchdogServiceMissing);
                _log.LogWarning(
                    "WatchdogServiceGuard: native maintenance repair launch {Result}",
                    launchAccepted ? "accepted" : "failed");
                return launchAccepted;
            default:
                return false;
        }
    }
}

/// <summary>
/// Production probe: <c>sc.exe queryex</c> detects registration (FAILED 1060 = not installed); the
/// Authenticode-signed native maintenance host beside the Broker performs privileged repair. No caller
/// can substitute a path through configuration or an environment variable.
/// </summary>
public sealed class ScWatchdogServiceProbe : IWatchdogServiceProbe
{
    private const string WatchdogServiceName = "SuavoAgent.Watchdog";
    private readonly string _installDirectory;
    private readonly string _maintenanceExecutablePath;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly Func<string, MaintenanceHostTrustResult> _verifyMaintenanceTrust;

    public ScWatchdogServiceProbe()
    {
        _installDirectory = ResolveInstallDirectory();
        _maintenanceExecutablePath = Path.Combine(
            _installDirectory,
            MaintenanceContract.ExecutableName);
        _fileExists = File.Exists;
        _startProcess = startInfo => Process.Start(startInfo);
        _verifyMaintenanceTrust = MaintenanceHostTrustVerifier.Verify;
    }

    internal ScWatchdogServiceProbe(
        string maintenanceExecutablePath,
        string installDirectory,
        Func<string, bool> fileExists,
        Func<ProcessStartInfo, Process?> startProcess,
        Func<string, MaintenanceHostTrustResult>? verifyMaintenanceTrust = null)
    {
        _maintenanceExecutablePath = maintenanceExecutablePath;
        _installDirectory = installDirectory;
        _fileExists = fileExists;
        _startProcess = startProcess;
        _verifyMaintenanceTrust = verifyMaintenanceTrust ?? MaintenanceHostTrustVerifier.Verify;
    }

    public string MaintenanceExecutablePath => _maintenanceExecutablePath;

    public bool IsWatchdogServiceInstalled()
    {
        var output = RunCapture("sc.exe", $"queryex \"{WatchdogServiceName}\"", TimeSpan.FromSeconds(10));
        // Fail-safe: if we can't determine state, assume installed so we do NOT trigger a false repair.
        if (output is null) return true;
        return !output.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryStartMaintenanceRepair(MaintenanceReason reason)
    {
        if (reason == MaintenanceReason.Unspecified)
            return false;

        // The executable path is derived from the running Broker location and the shared constant;
        // it is never accepted from cloud/config/environment. This path reports process-creation
        // acceptance only. Waiting for repair completion would deadlock: the child must stop Broker
        // before reconfiguring and restarting it.
        var maintenanceExecutable = MaintenanceExecutablePath;
        if (!IsExpectedMaintenanceExecutable(maintenanceExecutable, _installDirectory) ||
            !_fileExists(maintenanceExecutable))
        {
            return false;
        }

        var trust = _verifyMaintenanceTrust(maintenanceExecutable);
        if (!trust.IsTrusted)
        {
            Serilog.Log.Error(
                "Broker rejected native maintenance repair before SYSTEM launch: {TrustCode}",
                trust.Code);
            return false;
        }

        try
        {
            var startInfo = BuildMaintenanceRepairStartInfo(maintenanceExecutable, reason);
            // Process.Start returning a non-null wrapper means CreateProcess succeeded. Disposing
            // only releases our local wrapper/handles; it does not terminate or wait for the child.
            using var process = _startProcess(startInfo);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    internal static string ResolveInstallDirectory(string? brokerProcessPath = null)
    {
        var processPath = string.IsNullOrWhiteSpace(brokerProcessPath)
            ? Environment.ProcessPath
            : brokerProcessPath;
        var installDir = string.IsNullOrWhiteSpace(processPath)
            ? null
            : Path.GetDirectoryName(processPath);
        if (string.IsNullOrWhiteSpace(installDir))
            installDir = AppContext.BaseDirectory;
        return Path.GetFullPath(installDir);
    }

    internal static string ResolveMaintenanceExecutablePath(string? brokerProcessPath = null) =>
        Path.Combine(
            ResolveInstallDirectory(brokerProcessPath),
            MaintenanceContract.ExecutableName);

    internal static bool IsExpectedMaintenanceExecutable(
        string candidatePath,
        string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) ||
            string.IsNullOrWhiteSpace(installDirectory) ||
            !Path.IsPathFullyQualified(candidatePath))
        {
            return false;
        }

        try
        {
            var expected = Path.GetFullPath(
                Path.Combine(installDirectory, MaintenanceContract.ExecutableName));
            var actual = Path.GetFullPath(candidatePath);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static ProcessStartInfo BuildMaintenanceRepairStartInfo(
        string maintenanceExecutablePath,
        MaintenanceReason reason)
    {
        if (!Path.IsPathFullyQualified(maintenanceExecutablePath) ||
            !string.Equals(
                Path.GetFileName(maintenanceExecutablePath),
                MaintenanceContract.ExecutableName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Maintenance executable must use the canonical filename.",
                nameof(maintenanceExecutablePath));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = maintenanceExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(maintenanceExecutablePath)!,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(MaintenanceContract.RepairServicesSwitch);
        startInfo.ArgumentList.Add(MaintenanceContract.ReasonSwitch);
        startInfo.ArgumentList.Add(MaintenanceContract.ToWireValue(reason));
        return startInfo;
    }

    private static string? RunCapture(string fileName, string arguments, TimeSpan timeout) =>
        RunCommandAndCapture(fileName, arguments, timeout).output;

    /// <summary>
    /// Runs a bounded diagnostic command such as <c>sc.exe queryex</c> and captures its output.
    /// This completion-waiting path is deliberately separate from detached maintenance launch.
    /// Stdout/stderr are drained before WaitForExit so neither pipe can fill and block the query.
    /// </summary>
    private static (int exitCode, string? output) RunCommandAndCapture(
        string fileName,
        string arguments,
        TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = TrustedWindowsSystemBinary.Resolve(fileName),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(startInfo);
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
