using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.State;

internal static class ProductionAclBoundary
{
    [SupportedOSPlatform("windows")]
    internal static void ValidatePath(
        string filePath,
        string expectedFileName,
        bool fileMustExist)
    {
        if (string.IsNullOrWhiteSpace(expectedFileName) ||
            !string.Equals(Path.GetFileName(expectedFileName), expectedFileName, StringComparison.Ordinal))
            throw new ArgumentException("Protected state filename is invalid.", nameof(expectedFileName));
        var root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent"));
        var expected = Path.Combine(root, expectedFileName);
        if (!string.Equals(Path.GetFullPath(filePath), expected, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Protected agent state escaped the fixed ProgramData boundary.");
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The protected SuavoAgent ProgramData directory is missing.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The SuavoAgent ProgramData directory cannot be a reparse point.");

        ValidateDirectory(root);
        if (fileMustExist && !File.Exists(filePath))
            throw new FileNotFoundException("The protected agent state file is missing.", filePath);
        if (File.Exists(filePath)) ValidateFile(filePath);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateDirectory(string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        var dangerousWrites =
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
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || IsAllowedPrincipal(rule.IdentityReference))
                continue;
            if ((rule.FileSystemRights & dangerousWrites) != 0)
                throw new UnauthorizedAccessException("SuavoAgent ProgramData grants write access to an untrusted principal.");
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidateFile(string filePath)
    {
        if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("A protected agent state file cannot be a reparse point.");
        var security = new FileInfo(filePath).GetAccessControl(AccessControlSections.Access);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        var dangerous =
            FileSystemRights.ReadData |
            FileSystemRights.WriteData |
            FileSystemRights.AppendData |
            FileSystemRights.Delete |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        foreach (var rule in rules)
        {
            if (rule.AccessControlType != AccessControlType.Allow || IsAllowedPrincipal(rule.IdentityReference))
                continue;
            if ((rule.FileSystemRights & dangerous) != 0)
                throw new UnauthorizedAccessException("Protected agent state grants access to an untrusted principal.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAllowedPrincipal(IdentityReference identity)
    {
        if (identity is not SecurityIdentifier sid) return false;
        return IsAllowedSidValue(sid.Value);
    }

    internal static bool IsAllowedSidValue(string sidValue) =>
        string.Equals(sidValue, "S-1-5-18", StringComparison.Ordinal) ||
        string.Equals(sidValue, CoreServiceIdentity.ServiceSid, StringComparison.Ordinal) ||
        string.Equals(sidValue, "S-1-5-32-544", StringComparison.Ordinal);
}
