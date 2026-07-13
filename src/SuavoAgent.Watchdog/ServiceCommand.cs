using System.Diagnostics;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;

namespace SuavoAgent.Watchdog;

public enum ServiceState
{
    Unknown,
    Running,
    Stopped,
    StartPending,
    StopPending,
    NotInstalled
}

public interface IServiceCommand
{
    ServiceState Query(string serviceName);
    bool Start(string serviceName, TimeSpan timeout);
    bool Stop(string serviceName, TimeSpan timeout);
    bool InvokeRepair(MaintenanceReason reason, TimeSpan timeout);
    bool InvokeUpdateCoordinator(string requestPath) => false;
    bool InvokeUpdateCoordinatorResume(string claimPath) => false;
    bool InvokePioneerRxApprovalInstaller(string requestPath, TimeSpan timeout) => false;
    bool InvokePioneerRxApprovalBootstrap(string requestPath, TimeSpan timeout) => false;
}

public sealed class ServiceCommand : IServiceCommand
{
    private readonly string _maintenanceExecutablePath;
    private readonly string _expectedInstallDirectory;
    private readonly Func<string, bool> _fileExists;
    private readonly Func<string, MaintenanceHostTrustResult> _verifyMaintenanceTrust;
    private readonly Func<string, string, TimeSpan, int?> _runForExitCode;
    private readonly Func<string, string, bool> _runDetached;
    private readonly string _expectedActivationRequestPath;
    private readonly string _expectedActiveClaimPath;
    private readonly string _expectedPioneerRxApprovalRequestPath;
    private readonly string _expectedPioneerRxBootstrapRequestPath;

    public ServiceCommand()
        : this(
            ResolveMaintenanceExecutablePath(),
            ResolveInstallDirectory(),
            File.Exists,
            RunForExitCode,
            MaintenanceHostTrustVerifier.Verify,
            RunDetached,
            UpdateActivationContract.DefaultActivationRequestPath(),
            UpdateActivationContract.DefaultActiveClaimPath(),
            PioneerRxApprovalMaintenanceContract.DefaultRequestPath(),
            PioneerRxApprovalBootstrapContract.DefaultRequestPath())
    {
    }

    internal ServiceCommand(
        string maintenanceExecutablePath,
        string expectedInstallDirectory,
        Func<string, bool> fileExists,
        Func<string, string, TimeSpan, int?> runForExitCode,
        Func<string, MaintenanceHostTrustResult>? verifyMaintenanceTrust = null,
        Func<string, string, bool>? runDetached = null,
        string? expectedActivationRequestPath = null,
        string? expectedActiveClaimPath = null,
        string? expectedPioneerRxApprovalRequestPath = null,
        string? expectedPioneerRxBootstrapRequestPath = null)
    {
        _maintenanceExecutablePath = maintenanceExecutablePath;
        _expectedInstallDirectory = expectedInstallDirectory;
        _fileExists = fileExists;
        _runForExitCode = runForExitCode;
        _verifyMaintenanceTrust = verifyMaintenanceTrust ?? MaintenanceHostTrustVerifier.Verify;
        _runDetached = runDetached ?? RunDetached;
        _expectedActivationRequestPath = expectedActivationRequestPath
                                         ?? UpdateActivationContract.DefaultActivationRequestPath();
        _expectedActiveClaimPath = expectedActiveClaimPath
                                   ?? UpdateActivationContract.DefaultActiveClaimPath();
        _expectedPioneerRxApprovalRequestPath = expectedPioneerRxApprovalRequestPath
                                                ?? PioneerRxApprovalMaintenanceContract.DefaultRequestPath();
        _expectedPioneerRxBootstrapRequestPath = expectedPioneerRxBootstrapRequestPath
                                                 ?? PioneerRxApprovalBootstrapContract.DefaultRequestPath();
    }

    public ServiceState Query(string serviceName)
    {
        var output = RunCapture("sc.exe", $"queryex \"{serviceName}\"", TimeSpan.FromSeconds(10));
        if (output is null) return ServiceState.Unknown;
        if (output.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase)) return ServiceState.NotInstalled;
        return ParseState(output);
    }

    public bool Start(string serviceName, TimeSpan timeout)
    {
        var output = RunCapture("sc.exe", $"start \"{serviceName}\"", timeout);
        if (output is null) return false;
        // sc.exe start returns START_PENDING on success; RUNNING if already up.
        return output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)
            || output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public bool Stop(string serviceName, TimeSpan timeout)
    {
        // sc.exe stop returns STOP_PENDING (or 1062 "service not started") immediately;
        // we must WAIT for the process to actually exit before a subsequent Start, or the
        // start races a stopping service and SCM rejects it. Poll Query until STOPPED.
        var output = RunCapture("sc.exe", $"stop \"{serviceName}\"", TimeSpan.FromSeconds(10));
        // 1062 = "The service has not been started" — already stopped, success.
        if (output is not null && output.Contains("1062", StringComparison.OrdinalIgnoreCase))
            return true;

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = Query(serviceName);
            if (state is ServiceState.Stopped or ServiceState.NotInstalled)
                return true;
            Thread.Sleep(250);
        }
        return Query(serviceName) == ServiceState.Stopped;
    }

    public bool InvokeRepair(MaintenanceReason reason, TimeSpan timeout)
    {
        if (reason == MaintenanceReason.Unspecified || timeout <= TimeSpan.Zero)
            return false;

        // The only privileged repair target is the fixed maintenance host staged beside this
        // service. Reject a renamed, relocated, relative, or missing executable before launch.
        // This keeps a writable path or environment value from becoming a SYSTEM execution surface.
        if (!IsExpectedMaintenanceExecutable(_maintenanceExecutablePath, _expectedInstallDirectory) ||
            !_fileExists(_maintenanceExecutablePath))
            return false;

        var trust = _verifyMaintenanceTrust(_maintenanceExecutablePath);
        if (!trust.IsTrusted)
        {
            Serilog.Log.Error(
                "Native maintenance repair rejected before SYSTEM launch: {TrustCode}",
                trust.Code);
            return false;
        }

        var args = MaintenanceContract.BuildRepairArguments(reason);
        return _runForExitCode(_maintenanceExecutablePath, args, timeout) == 0;
    }

    public bool InvokeUpdateCoordinator(string requestPath)
    {
        if (!IsExpectedActivationRequest(requestPath, _expectedActivationRequestPath) ||
            !_fileExists(requestPath) ||
            !IsExpectedMaintenanceExecutable(_maintenanceExecutablePath, _expectedInstallDirectory) ||
            !_fileExists(_maintenanceExecutablePath))
            return false;

        var trust = _verifyMaintenanceTrust(_maintenanceExecutablePath);
        if (!trust.IsTrusted)
        {
            Serilog.Log.Error(
                "SYSTEM update coordinator rejected before launch: {TrustCode}",
                trust.Code);
            return false;
        }

        var args = $"{UpdateActivationContract.ActivateSwitch} " +
                   $"{UpdateActivationContract.RequestPathSwitch} \"{requestPath}\"";
        return _runDetached(_maintenanceExecutablePath, args);
    }

    public bool InvokeUpdateCoordinatorResume(string claimPath)
    {
        if (!IsExpectedActivationRequest(claimPath, _expectedActiveClaimPath) ||
            !_fileExists(claimPath) ||
            !IsExpectedMaintenanceExecutable(_maintenanceExecutablePath, _expectedInstallDirectory) ||
            !_fileExists(_maintenanceExecutablePath))
            return false;

        var trust = _verifyMaintenanceTrust(_maintenanceExecutablePath);
        if (!trust.IsTrusted)
        {
            Serilog.Log.Error(
                "SYSTEM update resume rejected before launch: {TrustCode}",
                trust.Code);
            return false;
        }

        var args = $"{UpdateActivationContract.ResumeSwitch} " +
                   $"{UpdateActivationContract.ClaimPathSwitch} \"{claimPath}\"";
        return _runDetached(_maintenanceExecutablePath, args);
    }

    public bool InvokePioneerRxApprovalInstaller(string requestPath, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero ||
            !PioneerRxApprovalMaintenanceContract.IsExactRequestPath(
                requestPath,
                _expectedPioneerRxApprovalRequestPath) ||
            !_fileExists(requestPath) ||
            !IsExpectedMaintenanceExecutable(_maintenanceExecutablePath, _expectedInstallDirectory) ||
            !_fileExists(_maintenanceExecutablePath))
            return false;

        var trust = _verifyMaintenanceTrust(_maintenanceExecutablePath);
        if (!trust.IsTrusted)
        {
            Serilog.Log.Error(
                "SYSTEM PioneerRx approval install rejected before launch: {TrustCode}",
                trust.Code);
            return false;
        }

        var arguments = $"{PioneerRxApprovalMaintenanceContract.InstallSwitch} " +
                        $"{PioneerRxApprovalMaintenanceContract.RequestPathSwitch} \"{requestPath}\"";
        return _runForExitCode(_maintenanceExecutablePath, arguments, timeout) == 0;
    }

    public bool InvokePioneerRxApprovalBootstrap(string requestPath, TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero ||
            !PioneerRxApprovalBootstrapContract.IsExactRequestPath(
                requestPath,
                _expectedPioneerRxBootstrapRequestPath) ||
            !_fileExists(requestPath) ||
            !IsExpectedMaintenanceExecutable(_maintenanceExecutablePath, _expectedInstallDirectory) ||
            !_fileExists(_maintenanceExecutablePath))
            return false;
        var trust = _verifyMaintenanceTrust(_maintenanceExecutablePath);
        if (!trust.IsTrusted) return false;
        var arguments = $"{PioneerRxApprovalBootstrapContract.BootstrapSwitch} " +
                        $"{PioneerRxApprovalBootstrapContract.RequestPathSwitch} \"{requestPath}\"";
        return _runForExitCode(_maintenanceExecutablePath, arguments, timeout) == 0;
    }

    internal static string ResolveInstallDirectory() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    internal static string ResolveMaintenanceExecutablePath() =>
        Path.Combine(ResolveInstallDirectory(), MaintenanceContract.ExecutableName);

    internal static bool IsExpectedMaintenanceExecutable(string candidatePath, string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(installDirectory))
            return false;

        try
        {
            if (!Path.IsPathFullyQualified(candidatePath))
                return false;
            if (!string.Equals(
                    Path.GetFileName(candidatePath),
                    MaintenanceContract.ExecutableName,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            var expected = Path.GetFullPath(Path.Combine(installDirectory, MaintenanceContract.ExecutableName));
            var actual = Path.GetFullPath(candidatePath);
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsExpectedActivationRequest(string candidatePath, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) ||
            string.IsNullOrWhiteSpace(expectedPath) ||
            !Path.IsPathFullyQualified(candidatePath) ||
            !Path.IsPathFullyQualified(expectedPath))
            return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(candidatePath),
                Path.GetFullPath(expectedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static ServiceState ParseState(string queryOutput)
    {
        if (queryOutput.Contains("STATE", StringComparison.OrdinalIgnoreCase))
        {
            if (queryOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return ServiceState.Running;
            if (queryOutput.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return ServiceState.Stopped;
            if (queryOutput.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)) return ServiceState.StartPending;
            if (queryOutput.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)) return ServiceState.StopPending;
        }
        return ServiceState.Unknown;
    }

    private static string? RunCapture(string fileName, string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = TrustedWindowsSystemBinary.Resolve(fileName),
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            return stdout.Result + stderr.Result;
        }
        catch
        {
            return null;
        }
    }

    // Returns the process EXIT CODE (null on launch failure / timeout / exception). Repair requires a
    // zero exit instead of "merely launched". Reads the pipes async so a chatty child can't fill a
    // buffer and block before exit.
    private static int? RunForExitCode(string fileName, string arguments, TimeSpan timeout)
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
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;

            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            try { System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { stdout, stderr }, 2000); } catch { }
            return p.ExitCode;
        }
        catch
        {
            return null;
        }
    }

    private static bool RunDetached(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
            });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}
