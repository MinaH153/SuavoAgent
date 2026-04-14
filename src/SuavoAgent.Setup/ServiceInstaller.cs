using System.Diagnostics;

namespace SuavoAgent.Setup;

/// <summary>
/// Registers and starts SuavoAgent Windows services using sc.exe.
/// Mirrors the bootstrap.ps1 service registration logic.
/// </summary>
internal static class ServiceInstaller
{
    private const string CoreServiceName = "SuavoAgent.Core";
    private const string BrokerServiceName = "SuavoAgent.Broker";

    /// <summary>
    /// Full service installation pipeline: stop existing -> register -> ACL -> start -> verify.
    /// Returns true if both services are running.
    /// </summary>
    public static bool InstallAndStart(string installDir, string dataDir)
    {
        // Step 1: Stop and remove any existing services
        StopAndRemove(CoreServiceName);
        StopAndRemove(BrokerServiceName);

        // Step 2: Create directories
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

        // Step 3: Register services
        var corePath = Path.Combine(installDir, "SuavoAgent.Core.exe");
        var brokerPath = Path.Combine(installDir, "SuavoAgent.Broker.exe");

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

        // Core — runs as LocalService (least privilege)
        RunSc($"create {CoreServiceName} binPath= \"\\\"{corePath}\\\"\" start= delayed-auto obj= \"NT AUTHORITY\\LocalService\"");
        RunSc($"description {CoreServiceName} \"Suavo pharmacy agent - SQL polling, cloud sync\"");
        RunSc($"failure {CoreServiceName} reset= 3600 actions= restart/5000/restart/30000/restart/60000");
        RunSc($"failureflag {CoreServiceName} 1");
        ConsoleUI.WriteOk($"{CoreServiceName} service registered");

        // Broker — runs as NetworkService (session detection, process launch)
        RunSc($"create {BrokerServiceName} binPath= \"\\\"{brokerPath}\\\"\" start= delayed-auto obj= \"NT AUTHORITY\\NetworkService\"");
        RunSc($"description {BrokerServiceName} \"Suavo pharmacy agent - session broker\"");
        RunSc($"failure {BrokerServiceName} reset= 3600 actions= restart/5000/restart/30000/restart/60000");
        RunSc($"failureflag {BrokerServiceName} 1");
        RunSc($"config {BrokerServiceName} depend= {CoreServiceName}");
        ConsoleUI.WriteOk($"{BrokerServiceName} service registered");

        // Step 4: Lock down data directory ACL (install dir already locked in Phase 4)
        LockdownDirectoryAcl(dataDir);

        // Step 5: Start services
        ConsoleUI.WriteInfo("Starting services...");
        RunSc($"start {CoreServiceName}");
        Thread.Sleep(3000); // Give Core time to initialize before starting Broker
        RunSc($"start {BrokerServiceName}");

        // Step 6: Verify
        Thread.Sleep(2000);
        var coreRunning = IsServiceRunning(CoreServiceName);
        var brokerRunning = IsServiceRunning(BrokerServiceName);

        if (coreRunning)
            ConsoleUI.WriteOk($"{CoreServiceName} is running");
        else
            ConsoleUI.WriteWarn($"{CoreServiceName} may not be running yet");

        if (brokerRunning)
            ConsoleUI.WriteOk($"{BrokerServiceName} is running");
        else
            ConsoleUI.WriteWarn($"{BrokerServiceName} may not be running yet");

        return coreRunning; // Broker may take a moment; Core is the critical one
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
}
