using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.State;

/// <summary>
/// Read-only runtime proof of the Setup-owned ProgramData boundary. Core may
/// refuse unsafe state but has no API that can create, repair, or mutate ACLs.
/// </summary>
internal static class InstalledDataRootVerifier
{
    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";
    private const string UsersSid = "S-1-5-32-545";

    [SupportedOSPlatform("windows")]
    private static FileSystemRights DangerousWriteRights =>
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

    public static bool IsSafe(string root)
    {
        try
        {
            if (!Directory.Exists(root) ||
                File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
                return false;
            return !OperatingSystem.IsWindows() || VerifyWindowsAcl(root);
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or ArgumentException or
                   NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static bool VerifyDescriptor(byte[] descriptor)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorBinaryForm(descriptor);
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier))?.Value != SystemSid)
            return false;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        if (rules.Length != 4 || rules.Any(rule =>
                rule.IsInherited ||
                rule.AccessControlType != AccessControlType.Allow ||
                rule.PropagationFlags != PropagationFlags.None))
            return false;
        var bySid = rules.ToDictionary(
            rule => rule.IdentityReference.Value,
            StringComparer.Ordinal);
        var inherited = InheritanceFlags.ContainerInherit |
                        InheritanceFlags.ObjectInherit;
        return Exact(bySid, SystemSid, FileSystemRights.FullControl, inherited) &&
               Exact(bySid, AdministratorsSid, FileSystemRights.FullControl, inherited) &&
               Exact(bySid, CoreServiceIdentity.ServiceSid, FileSystemRights.Modify, inherited) &&
               Exact(bySid, UsersSid, FileSystemRights.ReadAndExecute, InheritanceFlags.None) &&
               (bySid[UsersSid].FileSystemRights & DangerousWriteRights) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static bool VerifyWindowsAcl(string root)
    {
        var descriptor = new DirectoryInfo(root).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access)
            .GetSecurityDescriptorBinaryForm();
        return VerifyDescriptor(descriptor);
    }

    [SupportedOSPlatform("windows")]
    private static bool Exact(
        IReadOnlyDictionary<string, FileSystemAccessRule> rules,
        string sid,
        FileSystemRights rights,
        InheritanceFlags inheritance) =>
        rules.TryGetValue(sid, out var rule) &&
        rule.FileSystemRights == rights &&
        rule.InheritanceFlags == inheritance;
}
