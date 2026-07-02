using System.Diagnostics;

namespace SuavoAgent.Core.Vision;

/// <summary>
/// Grants the de-privileged Helper (BUILTIN\Users) READ access to the vision assets that Core (SYSTEM)
/// writes into the ACL-locked %ProgramData%\SuavoAgent folder — vision.json plus the vision\ dir (the
/// Tesseract native DLLs + tessdata). Without this the Helper, which runs as the interactive user,
/// gets "Access denied" reading vision.json and silently falls back to vision-off (observed on the box).
///
/// Mirrors <c>HelperExeAclGrant</c> (same *S-1-5-32-545 principal) but hardened: the paths originate
/// from a signed set_vision_config payload, so (1) each icacls argument is passed via ArgumentList (no
/// string interpolation → a path containing a quote can't inject extra args into the SYSTEM process),
/// and (2) both paths are validated to be canonical subpaths of %ProgramData%\SuavoAgent before the
/// grant runs, so a caller cannot redirect the recursive RX grant onto a sensitive directory.
/// </summary>
public static class VisionAssetsAclGrant
{
    // BUILTIN\Users (S-1-5-32-545) as a SID literal — locale-independent, and the de-priv Helper token
    // is always a member. Matches HelperExeAclGrant.HelperPrincipal.
    public const string Principal = "*S-1-5-32-545";

    private static string ProgramDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SuavoAgent");

    /// <summary>True iff <paramref name="path"/> canonicalizes to the SuavoAgent ProgramData root or a
    /// path beneath it — the only place these assets may live. Pure + unit-testable.</summary>
    public static bool IsUnderSuavoRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return full.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>vision.json → RX (single file). visionDir → (OI)(CI)(RX) recursive so the DLLs +
    /// tessdata inherit read. Each element is one already-split icacls argument (ArgumentList form).</summary>
    public static IReadOnlyList<string> JsonGrantArgs(string visionJsonPath) =>
        new[] { visionJsonPath, "/grant", $"{Principal}:(RX)" };

    public static IReadOnlyList<string> DirGrantArgs(string visionDir) =>
        new[] { visionDir, "/grant", $"{Principal}:(OI)(CI)(RX)", "/t" };

    /// <summary>Best-effort; never throws. Refuses (returns false) if either path is not under
    /// %ProgramData%\SuavoAgent. Returns true iff both grants succeed.</summary>
    public static bool Apply(string visionJsonPath, string visionDir, Action<string>? log = null)
    {
        var root = ProgramDataRoot();
        if (!IsUnderSuavoRoot(visionJsonPath, root) || !IsUnderSuavoRoot(visionDir, root))
        {
            log?.Invoke($"REFUSED vision ACL grant — path outside {root} (json={visionJsonPath}, dir={visionDir})");
            return false;
        }

        var jsonOk = File.Exists(visionJsonPath) && RunIcacls(JsonGrantArgs(visionJsonPath), log);
        var dirOk = Directory.Exists(visionDir) && RunIcacls(DirGrantArgs(visionDir), log);
        return jsonOk && dirOk;
    }

    private static bool RunIcacls(IReadOnlyList<string> args, Action<string>? log)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "icacls",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // ArgumentList quotes each element safely — a path containing a quote/space cannot break
            // out and inject additional icacls arguments into the SYSTEM process.
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) { log?.Invoke("icacls failed to start"); return false; }

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(); } catch { /* best-effort */ }
                log?.Invoke("icacls timed out (>30s)");
                return false;
            }
            if (p.ExitCode != 0)
            {
                log?.Invoke($"icacls exit {p.ExitCode}: {string.Join(' ', args)} :: {(stderr + stdout).Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"icacls threw: {ex.Message}");
            return false;
        }
    }
}
