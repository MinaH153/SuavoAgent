using System.Diagnostics;

namespace SuavoAgent.Diagnostics;

/// <summary>
/// Re-applies the BUILTIN\Users read+execute carve-out the de-privileged Helper needs to
/// self-extract its compressed single-file apphost (<c>SuavoAgent.Helper.exe</c>).
///
/// The INSTALLER applies this at install time (<c>ServiceInstaller.GrantInteractiveHelperExeAccess</c>,
/// which delegates here). An OTA binary swap (<c>SelfUpdater.SwapBinaries</c> → <c>File.Move</c>) lands
/// a fresh <c>Helper.exe</c> that carries only the install dir's <i>inheritable</i> ACEs
/// (SYSTEM/Admins/LocalService — NO Users), dropping the per-file grant; and Core, running as
/// <c>NT AUTHORITY\LocalService</c>, lacks WRITE_DAC to restore it. So the LocalSystem Watchdog
/// re-applies it post-OTA (before cycling the Broker that relaunches the Helper) and on its own startup.
///
/// Scoped IDENTICALLY to the installer: <b>traverse-on-dir + RX-on-Helper.exe ONLY</b> — no (OI)(CI),
/// so child files do NOT inherit read and <c>appsettings.json</c> (ApiKey + SQL creds) plus the other
/// service binaries stay UNREADABLE by local users. This is the single source of truth for the grant.
/// </summary>
public static class HelperExeAclGrant
{
    // BUILTIN\Users (S-1-5-32-545). The de-priv Helper token is ALWAYS a member (robust vs the
    // INTERACTIVE group S-1-5-4, which a UAC-filtered/edge-case token can lack). SID form =
    // locale-independent (icacls won't mis-resolve a localized "Users" on a non-English box).
    public const string HelperPrincipal = "*S-1-5-32-545";

    public const string HelperExeName = "SuavoAgent.Helper.exe";

    // One source of truth for each icacls argument string. Dir grant = traverse/list THIS DIR ONLY
    // (no (OI)(CI) → no inherited file reads); file grant = RX on the single-file apphost itself.
    internal static string DirGrantArgs(string installDir) =>
        $"\"{installDir}\" /grant \"{HelperPrincipal}:(RX)\"";

    internal static string FileGrantArgs(string helperExePath) =>
        $"\"{helperExePath}\" /grant \"{HelperPrincipal}:(RX)\"";

    /// <summary>The exact icacls argument strings, in order (dir, then Helper.exe). Pure +
    /// unit-testable — no side effects, no filesystem access.</summary>
    public static IReadOnlyList<string> BuildIcaclsArgs(string installDir) =>
        new[]
        {
            DirGrantArgs(installDir),
            FileGrantArgs(Path.Combine(installDir, HelperExeName)),
        };

    /// <summary>
    /// Applies the grants via icacls. Best-effort, never throws. Returns true iff the dir grant
    /// succeeds AND (the Helper.exe grant succeeds OR the apphost is legitimately absent).
    /// <paramref name="log"/> is an optional sink for human-readable progress/errors.
    /// </summary>
    public static bool Apply(string installDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            log?.Invoke($"install dir not found for Helper read-grant: {installDir}");
            return false;
        }

        var dirOk = RunIcacls(DirGrantArgs(installDir), log);

        var helperExe = Path.Combine(installDir, HelperExeName);
        if (!File.Exists(helperExe))
        {
            // No apphost to grant (e.g. a partial install) — the dir grant is all we can do.
            log?.Invoke($"Helper apphost not present (skipping per-file grant): {helperExe}");
            return dirOk;
        }

        var fileOk = RunIcacls(FileGrantArgs(helperExe), log);

        log?.Invoke(dirOk && fileOk
            ? "Helper apphost readable by the interactive user (single-file self-extract); appsettings stays protected"
            : "Helper apphost read-grant did NOT fully succeed — the Helper may churn (cannot self-extract)");

        return dirOk && fileOk;
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

            if (p is null)
            {
                log?.Invoke($"icacls failed to start: {arguments}");
                return false;
            }

            // Read streams before WaitForExit to avoid a full-pipe deadlock on verbose output.
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
