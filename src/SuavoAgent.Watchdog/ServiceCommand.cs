using System.Diagnostics;

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
    bool InvokeRepair(string bootstrapPath, TimeSpan timeout);
}

public sealed class ServiceCommand : IServiceCommand
{
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

    public bool InvokeRepair(string bootstrapPath, TimeSpan timeout)
    {
        // PowerShell 5.1+ is a hard prereq for bootstrap.ps1 (enforced inside the script).
        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{bootstrapPath}\" --repair";
        var output = RunCapture("powershell.exe", args, timeout);
        return output is not null;
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
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
