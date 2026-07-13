using System.Security.AccessControl;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Diagnostics;

/// <summary>
/// Exact least-privilege access the de-privileged Helper needs to self-extract
/// its signed single-file apphost. Every mutation is bound to a no-follow
/// handle; appsettings and all other service binaries remain unreadable.
/// </summary>
public static class HelperExeAclGrant
{
    public const string HelperSid = "S-1-5-32-545";
    public const string HelperExeName = "SuavoAgent.Helper.exe";

    public static IReadOnlyList<HandleBoundAclMutation> BuildMutations(
        string installDir,
        bool includeHelper = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        const InheritanceFlags inherited =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var root = new HandleBoundAclPolicy(HandleBoundAcl.SystemSid,
        [
            new(HandleBoundAcl.SystemSid, FileSystemRights.FullControl, inherited),
            new(HandleBoundAcl.AdministratorsSid, FileSystemRights.FullControl, inherited),
            new(CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute, inherited),
            new(HelperSid, FileSystemRights.ReadAndExecute),
        ]);
        var mutations = new List<HandleBoundAclMutation>
        {
            new(installDir, IsDirectory: true, root),
        };
        if (includeHelper)
        {
            mutations.Add(new(
                Path.Combine(installDir, HelperExeName),
                IsDirectory: false,
                new(HandleBoundAcl.SystemSid,
                [
                    new(HandleBoundAcl.SystemSid, FileSystemRights.FullControl),
                    new(HandleBoundAcl.AdministratorsSid, FileSystemRights.FullControl),
                    new(CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute),
                    new(HelperSid, FileSystemRights.ReadAndExecute),
                ])));
        }
        return mutations;
    }

    /// <summary>
    /// Best-effort compatibility wrapper used by the LocalSystem Watchdog.
    /// Returns false on any identity, ACL, or platform failure.
    /// </summary>
    public static bool Apply(string installDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
        {
            log?.Invoke($"install dir not found for Helper read-grant: {installDir}");
            return false;
        }

        var helperExe = Path.Combine(installDir, HelperExeName);
        var includeHelper = File.Exists(helperExe);
        try
        {
            new HandleBoundAcl().ApplyBatch(BuildMutations(installDir, includeHelper));
            log?.Invoke(includeHelper
                ? "Helper apphost readable by the interactive user; appsettings stays protected"
                : $"Helper apphost not present (root traverse only): {helperExe}");
            return true;
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or
                   SystemException or ArgumentException)
        {
            log?.Invoke($"handle-bound Helper ACL failed: {exception.GetType().Name}");
            return false;
        }
    }
}
