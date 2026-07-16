using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Watchdog;

/// <summary>
/// Breaks a stale claim's exclusive runner lease before resume. The only killable image is the
/// exact Maintenance runner inside the SYSTEM/Admin-only directory for that claim's 64-hex staging
/// id. Installed Maintenance and arbitrary same-name processes are never termination candidates.
/// </summary>
internal static class StaleUpdateRunnerTerminator
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(30);

    public static bool TerminateExact(
        string maintenanceRoot,
        string stagingId,
        ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return false;
        string expectedPath;
        try
        {
            if (!Path.IsPathFullyQualified(maintenanceRoot)) return false;
            expectedPath = Path.GetFullPath(
                UpdateActivationContract.GetMaintenanceRunnerPath(
                    maintenanceRoot,
                    stagingId));
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            var processName = Path.GetFileNameWithoutExtension(
                MaintenanceContract.ExecutableName);
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    string? imagePath;
                    try { imagePath = process.MainModule?.FileName; }
                    catch { continue; }
                    if (!IsExactRunnerImage(imagePath, expectedPath)) continue;

                    try
                    {
                        logger.LogWarning(
                            "Terminating stale SYSTEM update runner pid={ProcessId} before claim resume",
                            process.Id);
                        process.Kill(entireProcessTree: true);
                        if (!process.WaitForExit((int)ExitTimeout.TotalMilliseconds))
                            return false;
                    }
                    catch (InvalidOperationException)
                    {
                        // It exited after path verification and released the file lease.
                    }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogSafeError(ex);
            return false;
        }
    }

    internal static bool IsExactRunnerImage(string? candidatePath, string expectedPath)
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
}
