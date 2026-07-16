using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Contracts.Reasoning;

public sealed record BrainCohortAclVerification(bool IsValid, string Code);

/// <summary>
/// Makes installed model weights and executable native libraries immutable to
/// the Core service. Setup/Admin and SYSTEM retain maintenance authority; the
/// exact Core service SID receives read/execute only.
/// </summary>
public static class BrainCohortAcl
{
    public const int MaxEntries = 4_096;
    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";
    [SupportedOSPlatform("windows")]
    private static readonly FileSystemRights DangerousCoreRights =
        FileSystemRights.WriteData |
        FileSystemRights.CreateFiles |
        FileSystemRights.CreateDirectories |
        FileSystemRights.AppendData |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;

    [SupportedOSPlatform("windows")]
    public static BrainCohortAclVerification ProtectAndVerify(string cohortRoot)
    {
        try
        {
            new HandleBoundAcl().ApplyTree(
                cohortRoot,
                DirectoryPolicy(),
                FilePolicy(),
                HandleBoundAcl.WithoutInheritance(DirectoryPolicy()),
                maximumEntries: MaxEntries);
            return Verify(cohortRoot);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or
            SystemException or ArgumentException)
        {
            return new(false, "brain_acl_apply_failed");
        }
    }

    [SupportedOSPlatform("windows")]
    public static BrainCohortAclVerification Verify(string cohortRoot)
    {
        try
        {
            var root = new DirectoryInfo(Path.GetFullPath(cohortRoot));
            if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !VerifyRules(root.GetAccessControl(
                    AccessControlSections.Owner | AccessControlSections.Access)))
                return new(false, "brain_acl_root_invalid");
            var entries = EnumerateBounded(root.FullName);
            if (entries is null) return new(false, "brain_acl_tree_invalid");
            foreach (var entry in entries)
            {
                FileSystemSecurity security = entry.IsDirectory
                    ? new DirectoryInfo(entry.Path).GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Access)
                    : new FileInfo(entry.Path).GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Access);
                if (!VerifyRules(security))
                    return new(false, "brain_acl_entry_invalid");
            }
            return new(true, "valid");
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or
            SystemException or ArgumentException)
        {
            return new(false, "brain_acl_verify_failed");
        }
    }

    [SupportedOSPlatform("windows")]
    internal static bool VerifyRules(FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier))?.Value != SystemSid)
            return false;
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Length != 3 || rules.Any(rule =>
                rule.AccessControlType != AccessControlType.Allow || rule.IsInherited))
            return false;

        var bySid = rules.ToDictionary(
            rule => rule.IdentityReference.Value,
            StringComparer.Ordinal);
        if (!bySid.TryGetValue(SystemSid, out var system) ||
            !bySid.TryGetValue(AdministratorsSid, out var administrators) ||
            !bySid.TryGetValue(CoreServiceIdentity.ServiceSid, out var core))
            return false;
        return (system.FileSystemRights & FileSystemRights.FullControl) ==
                   FileSystemRights.FullControl &&
               (administrators.FileSystemRights & FileSystemRights.FullControl) ==
                   FileSystemRights.FullControl &&
               (core.FileSystemRights & FileSystemRights.ReadAndExecute) ==
                   FileSystemRights.ReadAndExecute &&
               (core.FileSystemRights & DangerousCoreRights) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static HandleBoundAclPolicy DirectoryPolicy()
    {
        const InheritanceFlags inherited =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        return new(HandleBoundAcl.SystemSid,
        [
            new(SystemSid, FileSystemRights.FullControl, inherited),
            new(AdministratorsSid, FileSystemRights.FullControl, inherited),
            new(CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute, inherited),
        ]);
    }

    [SupportedOSPlatform("windows")]
    private static HandleBoundAclPolicy FilePolicy() => new(
        HandleBoundAcl.SystemSid,
    [
        new(SystemSid, FileSystemRights.FullControl),
        new(AdministratorsSid, FileSystemRights.FullControl),
        new(CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute),
    ]);

    private static IReadOnlyList<(string Path, bool IsDirectory)>? EnumerateBounded(
        string root)
    {
        var entries = new List<(string Path, bool IsDirectory)>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (entries.Count >= MaxEntries) return null;
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                entries.Add((entry, isDirectory));
                if (isDirectory) pending.Push(entry);
            }
        }
        return entries;
    }
}
