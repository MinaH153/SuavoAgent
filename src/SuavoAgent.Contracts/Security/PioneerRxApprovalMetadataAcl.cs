using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Dedicated non-PHI signed-metadata boundary. The authority directory is never
/// Core-writable: SYSTEM/Admin install, Core reads, and interactive users read the
/// signed receipt/catalog needed by Helper. High-water state is not user-readable.
/// </summary>
public static class PioneerRxApprovalMetadataAcl
{
    public const string AuthorityDirectoryName = "pioneerrx-authority";
    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string BuiltinUsersSid = "S-1-5-32-545";

    [SupportedOSPlatform("windows")]
    public static void ProtectDirectory(string path)
    {
        new HandleBoundAcl().ApplyBatch(
        [
            new(path, IsDirectory: true, DirectoryPolicy()),
        ]);
        if (!ValidateDirectory(path))
            throw new UnauthorizedAccessException("PioneerRx authority directory ACL verification failed.");
    }

    [SupportedOSPlatform("windows")]
    public static void ProtectMetadataFile(string path) => ProtectFile(path, interactiveRead: true);

    [SupportedOSPlatform("windows")]
    public static void ProtectHighWaterFile(string path) => ProtectFile(path, interactiveRead: false);

    [SupportedOSPlatform("windows")]
    public static bool ValidateDirectory(string path)
    {
        if (!Directory.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return false;
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        if (!IsProtectedSystemOwned(security)) return false;
        var rules = Rules(security);
        const InheritanceFlags inherited =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        return ExactRules(rules,
            (SystemSid, FileSystemRights.FullControl, inherited),
            (AdministratorsSid, FileSystemRights.FullControl, inherited),
            (CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute, inherited),
            (BuiltinUsersSid, FileSystemRights.ReadAndExecute, inherited));
    }

    [SupportedOSPlatform("windows")]
    public static bool ValidateFile(string path, bool interactiveRead)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return false;
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Owner | AccessControlSections.Access);
        if (!IsProtectedSystemOwned(security)) return false;
        var rules = Rules(security);
        return interactiveRead
            ? ExactRules(rules,
                (SystemSid, FileSystemRights.FullControl, InheritanceFlags.None),
                (AdministratorsSid, FileSystemRights.FullControl, InheritanceFlags.None),
                (CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute, InheritanceFlags.None),
                (BuiltinUsersSid, FileSystemRights.ReadAndExecute, InheritanceFlags.None))
            : ExactRules(rules,
                (SystemSid, FileSystemRights.FullControl, InheritanceFlags.None),
                (AdministratorsSid, FileSystemRights.FullControl, InheritanceFlags.None),
                (CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute, InheritanceFlags.None));
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFile(string path, bool interactiveRead)
    {
        new HandleBoundAcl().ApplyBatch(
        [
            new(path, IsDirectory: false, FilePolicy(interactiveRead)),
        ]);
        if (!ValidateFile(path, interactiveRead))
            throw new UnauthorizedAccessException("PioneerRx authority ACL verification failed.");
    }

    [SupportedOSPlatform("windows")]
    private static bool ExactRules(
        IReadOnlyCollection<FileSystemAccessRule> rules,
        params (string Sid, FileSystemRights Rights, InheritanceFlags Inheritance)[] expected)
    {
        if (rules.Count != expected.Length || rules.Any(rule =>
                rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow ||
                rule.PropagationFlags != PropagationFlags.None))
            return false;
        return expected.All(item => rules.Count(rule =>
            string.Equals(rule.IdentityReference.Value, item.Sid, StringComparison.Ordinal) &&
            rule.FileSystemRights == item.Rights &&
            rule.InheritanceFlags == item.Inheritance) == 1);
    }

    [SupportedOSPlatform("windows")]
    private static HandleBoundAclPolicy DirectoryPolicy()
    {
        const InheritanceFlags inherited =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        return new(SystemSid,
        [
            new(SystemSid, FileSystemRights.FullControl, inherited),
            new(AdministratorsSid, FileSystemRights.FullControl, inherited),
            new(CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute, inherited),
            new(BuiltinUsersSid, FileSystemRights.ReadAndExecute, inherited),
        ]);
    }

    [SupportedOSPlatform("windows")]
    private static HandleBoundAclPolicy FilePolicy(bool interactiveRead)
    {
        var aces = new List<HandleBoundAclAce>
        {
            new(SystemSid, FileSystemRights.FullControl),
            new(AdministratorsSid, FileSystemRights.FullControl),
            new(CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute),
        };
        if (interactiveRead)
            aces.Add(new(BuiltinUsersSid, FileSystemRights.ReadAndExecute));
        return new(SystemSid, aces);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsProtectedSystemOwned(FileSystemSecurity security) =>
        security.AreAccessRulesProtected &&
        security.GetOwner(typeof(SecurityIdentifier))?.Value == SystemSid;

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule[] Rules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
}
