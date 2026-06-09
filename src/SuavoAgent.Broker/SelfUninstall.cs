using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Broker;

/// <summary>
/// Dashboard-triggered self-uninstall ("the agent removes itself"). Core runs as LocalService and
/// CANNOT delete services or wipe admin-owned directories, so on a <c>self_uninstall</c> command it
/// drops a sentinel file; the Broker (LocalSystem) — polled here — sees it and launches a detached
/// SYSTEM cleaner via a one-time scheduled task. The task is hosted by Task Scheduler, so it
/// SURVIVES the Broker's own deletion, and the cleaner disables the Watchdog FIRST so nothing
/// resurrects the agent mid-wipe. Privilege-correct + watchdog-safe — fixes what the legacy
/// in-process decommission path got wrong (it touched only Core/Broker and ran without rights).
/// </summary>
internal static class SelfUninstall
{
    internal const string TaskName = "SuavoSelfUninstall";

    /// <summary>Sentinel Core drops (it has Modify on the data dir); the Broker polls for it.</summary>
    internal static string SentinelPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent", "uninstall.request");

    /// <summary>A durable completion marker the cleaner writes OUTSIDE the wiped tree, for verification.</summary>
    internal static string DoneMarkerPath() => Path.Combine(
        Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Temp", "suavo_uninstall_done.txt");

    /// <summary>
    /// Pure (testable): the SYSTEM cleaner PowerShell, with the install dir + done-marker baked in.
    /// Order: disable -> stop -> kill Helper -> delete services (watchdog FIRST) -> wipe data + install
    /// dirs -> remove Suavo scheduled tasks (except itself) + registry -> write done-marker -> self-delete.
    /// </summary>
    internal static string BuildCleanerScript(string installDir, string doneMarkerPath)
    {
        var safeInstall = (installDir ?? string.Empty).Replace("'", "''");
        var safeMarker = (doneMarkerPath ?? string.Empty).Replace("'", "''");
        return $@"$ErrorActionPreference = 'SilentlyContinue'
Start-Sleep -Seconds 5
$svc = @('SuavoAgent.Watchdog','SuavoAgent.Broker','SuavoAgent.Core')   # watchdog FIRST (resurrector)
foreach($s in $svc){{ sc.exe config $s start= disabled | Out-Null }}
foreach($s in $svc){{ sc.exe stop $s | Out-Null }}
Start-Sleep -Seconds 3
Get-Process | Where-Object {{ $_.Name -like 'SuavoAgent.*' }} | ForEach-Object {{ Stop-Process -Id $_.Id -Force }}
Start-Sleep -Seconds 2
foreach($s in $svc){{ sc.exe delete $s | Out-Null }}
$pd = Join-Path $env:ProgramData 'SuavoAgent'
$install = '{safeInstall}'
foreach($d in @($pd,$install)){{ if($d -and (Test-Path $d)){{ Remove-Item $d -Recurse -Force }} }}
Get-ScheduledTask | Where-Object {{ $_.TaskName -like '*Suavo*' -and $_.TaskName -ne '{TaskName}' }} | ForEach-Object {{ Unregister-ScheduledTask -TaskName $_.TaskName -TaskPath $_.TaskPath -Confirm:$false }}
Remove-Item 'HKLM:\SOFTWARE\SuavoAgent' -Recurse -Force
""SUAVO_SELF_UNINSTALL_DONE $(Get-Date -Format o)"" | Out-File '{safeMarker}' -Force
schtasks /delete /tn {TaskName} /f | Out-Null
Remove-Item $MyInvocation.MyCommand.Path -Force
";
    }

    /// <summary>
    /// Windows-only side effect: write the cleaner to %WINDIR%\Temp (no spaces) and launch it as
    /// SYSTEM via a one-time scheduled task. Returns true if scheduled. Idempotency is the caller's
    /// responsibility (SessionWatcher fires this once).
    /// </summary>
    internal static bool TryLaunchCleaner(string installDir, ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var tempDir = Path.Combine(Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows", "Temp");
            Directory.CreateDirectory(tempDir);
            var cleaner = Path.Combine(tempDir, $"suavo_selfuninstall_{Guid.NewGuid():N}.ps1"); // no spaces
            File.WriteAllText(cleaner, BuildCleanerScript(installDir, DoneMarkerPath()));

            // /tr value has a space-free path, so no inner quoting needed; ArgumentList quotes the rest.
            var tr = $"powershell -NoProfile -ExecutionPolicy Bypass -File {cleaner}";
            RunSchtasks(logger, "/create", "/tn", TaskName, "/tr", tr, "/sc", "ONCE", "/st", "23:59",
                "/ru", "SYSTEM", "/rl", "HIGHEST", "/f");
            RunSchtasks(logger, "/run", "/tn", TaskName);
            logger.LogWarning("Self-uninstall: SYSTEM cleaner scheduled + launched ({Cleaner})", cleaner);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Self-uninstall: failed to schedule cleaner");
            return false;
        }
    }

    private static void RunSchtasks(ILogger logger, params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        p?.WaitForExit(15000);
    }
}
