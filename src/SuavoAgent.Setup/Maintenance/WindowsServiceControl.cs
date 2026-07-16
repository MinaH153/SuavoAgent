using System.Diagnostics;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Maintenance;

internal enum NativeServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    Running,
    StartPending,
    StopPending,
}

internal sealed record NativeServiceSpec(
    string Name,
    string ExecutableName,
    string Account,
    string Description,
    string FailureActions,
    string? Dependency = null,
    bool RequiresUnrestrictedServiceSid = false);

internal static class NativeServiceSpecs
{
    public static readonly NativeServiceSpec Core = new(
        CoreServiceIdentity.ServiceName,
        CoreServiceIdentity.ExecutableName,
        CoreServiceIdentity.AccountName,
        "Suavo pharmacy agent - SQL polling, cloud sync",
        "restart/5000/restart/30000/restart/60000",
        RequiresUnrestrictedServiceSid: true);

    public static readonly NativeServiceSpec Broker = new(
        "SuavoAgent.Broker",
        "SuavoAgent.Broker.exe",
        "LocalSystem",
        "Suavo pharmacy agent - session broker",
        "restart/5000/restart/30000/restart/60000",
        Core.Name);

    public static readonly NativeServiceSpec Watchdog = new(
        "SuavoAgent.Watchdog",
        "SuavoAgent.Watchdog.exe",
        "LocalSystem",
        "Suavo pharmacy agent - native process watchdog and maintenance coordinator",
        "restart/10000/restart/60000/restart/300000");

    public static readonly IReadOnlyList<NativeServiceSpec> All = [Core, Broker, Watchdog];
}

internal interface IWindowsServiceControl
{
    NativeServiceState Query(string serviceName);
    bool StopAndWait(string serviceName, TimeSpan timeout);
    bool EnsureConfigured(NativeServiceSpec spec, string installDir);
    bool StartAndWait(string serviceName, TimeSpan timeout);
}

/// <summary>
/// Transitional native Windows service control used until MSI owns the services.
/// It invokes the inbox Service Control utility directly; it never invokes a shell
/// or script host. Every mutating command must return exit code zero.
/// </summary>
internal sealed class ScWindowsServiceControl : IWindowsServiceControl
{
    public NativeServiceState Query(string serviceName)
    {
        var result = RunSc($"queryex \"{serviceName}\"", TimeSpan.FromSeconds(10));
        if (result.Output.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase))
            return NativeServiceState.NotInstalled;
        if (result.ExitCode != 0)
            return NativeServiceState.Unknown;
        if (result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
            return NativeServiceState.Running;
        if (result.Output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
            return NativeServiceState.Stopped;
        if (result.Output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
            return NativeServiceState.StartPending;
        if (result.Output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
            return NativeServiceState.StopPending;
        return NativeServiceState.Unknown;
    }

    public bool StopAndWait(string serviceName, TimeSpan timeout)
    {
        var state = Query(serviceName);
        if (state is NativeServiceState.NotInstalled or NativeServiceState.Stopped)
            return true;
        if (state == NativeServiceState.Unknown)
            return false;

        var stop = RunSc($"stop \"{serviceName}\"", TimeSpan.FromSeconds(15));
        if (stop.ExitCode != 0 && !stop.Output.Contains("1062", StringComparison.OrdinalIgnoreCase))
            return false;
        return WaitFor(serviceName, NativeServiceState.Stopped, timeout);
    }

    public bool EnsureConfigured(NativeServiceSpec spec, string installDir)
    {
        var executablePath = Path.Combine(installDir, spec.ExecutableName);
        if (!File.Exists(executablePath)) return false;

        var state = Query(spec.Name);
        if (state == NativeServiceState.Unknown) return false;
        if (state == NativeServiceState.NotInstalled)
        {
            var create = RunSc(
                $"create \"{spec.Name}\" binPath= \"\\\"{executablePath}\\\"\" start= delayed-auto obj= \"{spec.Account}\"",
                TimeSpan.FromSeconds(30));
            if (create.ExitCode != 0) return false;
        }

        // Reassert every security- and recovery-relevant property on every repair.
        // This is intentionally idempotent and never deletes a service definition.
        if (RunSc(
                $"config \"{spec.Name}\" binPath= \"\\\"{executablePath}\\\"\" start= delayed-auto obj= \"{spec.Account}\"",
                TimeSpan.FromSeconds(30)).ExitCode != 0)
            return false;

        // The account remains shared LocalService, but protected resources trust
        // only the per-service SID Windows adds to the token. Reassert this on
        // every repair so old installs are upgraded before exact-SID ACLs land.
        if (spec.RequiresUnrestrictedServiceSid)
        {
            if (RunSc(
                    $"sidtype \"{spec.Name}\" unrestricted",
                    TimeSpan.FromSeconds(15)).ExitCode != 0)
                return false;
            var sidType = RunSc($"qsidtype \"{spec.Name}\"", TimeSpan.FromSeconds(15));
            if (sidType.ExitCode != 0 ||
                !sidType.Output.Contains("UNRESTRICTED", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var dependency = spec.Dependency is null ? "/" : spec.Dependency;
        if (RunSc($"config \"{spec.Name}\" depend= \"{dependency}\"", TimeSpan.FromSeconds(15)).ExitCode != 0)
            return false;
        if (RunSc($"description \"{spec.Name}\" \"{spec.Description}\"", TimeSpan.FromSeconds(15)).ExitCode != 0)
            return false;
        if (RunSc(
                $"failure \"{spec.Name}\" reset= 3600 actions= {spec.FailureActions}",
                TimeSpan.FromSeconds(15)).ExitCode != 0)
            return false;
        if (RunSc($"failureflag \"{spec.Name}\" 1", TimeSpan.FromSeconds(15)).ExitCode != 0)
            return false;

        return Query(spec.Name) != NativeServiceState.NotInstalled;
    }

    public bool StartAndWait(string serviceName, TimeSpan timeout)
    {
        var state = Query(serviceName);
        if (state == NativeServiceState.Running) return true;
        if (state is NativeServiceState.NotInstalled or NativeServiceState.Unknown) return false;

        if (state != NativeServiceState.StartPending)
        {
            var start = RunSc($"start \"{serviceName}\"", TimeSpan.FromSeconds(15));
            if (start.ExitCode != 0 &&
                !start.Output.Contains("1056", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return WaitFor(serviceName, NativeServiceState.Running, timeout);
    }

    private bool WaitFor(string serviceName, NativeServiceState expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Query(serviceName) == expected) return true;
            Thread.Sleep(250);
        }
        return Query(serviceName) == expected;
    }

    private static ScResult RunSc(string arguments, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(
                TrustedWindowsSystemBinary.Resolve("sc.exe"),
                arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) return new(-1, string.Empty);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new(-1, string.Empty);
            }
            try { Task.WaitAll([stdout, stderr], TimeSpan.FromSeconds(2)); } catch { }
            return new(process.ExitCode, stdout.Result + stderr.Result);
        }
        catch
        {
            return new(-1, string.Empty);
        }
    }

    private sealed record ScResult(int ExitCode, string Output);
}
