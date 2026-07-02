using System.Diagnostics;

namespace SuavoAgent.Core.Vision;

/// <summary>
/// Grants the de-privileged Helper (BUILTIN\Users) READ access to the vision assets that Core (SYSTEM)
/// writes into the ACL-locked %ProgramData%\SuavoAgent folder — vision.json plus the vision\ dir (the
/// Tesseract native DLLs + tessdata). Without this the Helper, which runs as the interactive user,
/// gets "Access denied" reading vision.json and silently falls back to vision-off (observed on the box).
///
/// Mirrors <c>HelperExeAclGrant</c> (same *S-1-5-32-545 principal, same icacls mechanism) but scoped to
/// the vision assets — all non-sensitive (config + OCR binaries + traineddata), so a recursive
/// (OI)(CI)(RX) on the vision dir is safe (contrast the Helper.exe grant, which avoids inheritance to
/// keep appsettings secrets unreadable).
/// </summary>
public static class VisionAssetsAclGrant
{
    // BUILTIN\Users (S-1-5-32-545) as a SID literal — locale-independent, and the de-priv Helper token
    // is always a member. Matches HelperExeAclGrant.HelperPrincipal.
    public const string Principal = "*S-1-5-32-545";

    /// <summary>vision.json → RX (single file). visionDir → (OI)(CI)(RX) recursive so the DLLs +
    /// tessdata inherit read. Pure + unit-testable.</summary>
    public static IReadOnlyList<string> BuildIcaclsArgs(string visionJsonPath, string visionDir) =>
        new[]
        {
            $"\"{visionJsonPath}\" /grant \"{Principal}:(RX)\"",
            $"\"{visionDir}\" /grant \"{Principal}:(OI)(CI)(RX)\" /t",
        };

    /// <summary>Best-effort; never throws. Returns true iff both grants succeed.</summary>
    public static bool Apply(string visionJsonPath, string visionDir, Action<string>? log = null)
    {
        var jsonOk = File.Exists(visionJsonPath) && RunIcacls(BuildIcaclsArgs(visionJsonPath, visionDir)[0], log);
        var dirOk = Directory.Exists(visionDir) && RunIcacls(BuildIcaclsArgs(visionJsonPath, visionDir)[1], log);
        return jsonOk && dirOk;
    }

    private static bool RunIcacls(string arguments, Action<string>? log)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "icacls",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) { log?.Invoke($"icacls failed to start: {arguments}"); return false; }

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(); } catch { /* best-effort */ }
                log?.Invoke($"icacls timed out (>30s): {arguments}");
                return false;
            }
            if (p.ExitCode != 0)
            {
                log?.Invoke($"icacls exit {p.ExitCode}: {arguments} :: {(stderr + stdout).Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"icacls threw: {ex.Message} ({arguments})");
            return false;
        }
    }
}
