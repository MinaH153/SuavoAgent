using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using SuavoAgent.Contracts.Security;
namespace SuavoAgent.Diagnostics.Maintenance;

public sealed record PrivilegedStagedExecutable(
    string DirectoryPath,
    string ExecutablePath,
    string Sha256);

/// <summary>
/// Creates short-lived executable handoff directories that are inaccessible to
/// the unelevated user. The directory is born with its protected ACL, rather
/// than being created in a writable location and hardened afterward.
/// </summary>
public static class PrivilegedExecutableStaging
{
    public const string DirectoryPrefix = "SuavoAgent-Privileged-";
    public const string UninstallFilePrefix = "suavo-uninstall-";
    public const string VcRedistFilePrefix = "vc_redist.x64-";
    public const string MicrosoftPublisher = "Microsoft Corporation";
    private const string SystemSidValue = "S-1-5-18";
    private const string AdministratorsSidValue = "S-1-5-32-544";
    private const int AlreadyExists = 183;
    private const int MaximumCreateAttempts = 8;

    [SupportedOSPlatform("windows")]
    public static string CreateDirectory(params string[] additionalExcludedRoots)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Privileged executable staging is Windows-only.");

        var commonData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        var tempRoot = Path.GetTempPath();
        var excludedRoots = DefaultExcludedRoots(commonData)
            .Concat(additionalExcludedRoots ?? [])
            .ToArray();
        ValidateExistingAncestors(commonData);

        for (var attempt = 0; attempt < MaximumCreateAttempts; attempt++)
        {
            var directory = Path.Combine(
                commonData,
                DirectoryPrefix + Guid.NewGuid().ToString("N"));
            if (!IsApprovedStagingDirectory(
                    directory,
                    commonData,
                    tempRoot,
                    excludedRoots))
                throw new UnauthorizedAccessException(
                    "Privileged staging directory escaped its approved boundary.");

            if (!CreateDirectoryWithProtectedAcl(directory))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == AlreadyExists) continue;
                throw new IOException(
                    "Windows could not create the protected staging directory.",
                    new System.ComponentModel.Win32Exception(error));
            }

            try
            {
                ProtectDirectory(directory);
                if (!ValidateDirectory(directory))
                    throw new UnauthorizedAccessException(
                        "Privileged staging directory validation failed.");
                return directory;
            }
            catch
            {
                TryCleanupDirectory(directory);
                throw;
            }
        }

        throw new IOException("Windows could not allocate a unique staging directory.");
    }

    [SupportedOSPlatform("windows")]
    public static PrivilegedStagedExecutable StageMkmExecutable(
        string sourcePath,
        params string[] additionalExcludedRoots)
    {
        var source = Path.GetFullPath(sourcePath);
        var expectedSha256 = ComputeSha256(source);
        return StageVerifiedMkmExecutable(
            source,
            expectedSha256,
            UninstallFilePrefix,
            additionalExcludedRoots);
    }

    /// <summary>
    /// Copies only the exact bytes already authorized by a signed release or
    /// OTA receipt. Rechecking the supplied digest before and after the copy
    /// closes the install-tree check/use interval.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PrivilegedStagedExecutable StageVerifiedMkmExecutable(
        string sourcePath,
        string expectedSha256,
        string filePrefix,
        params string[] additionalExcludedRoots)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!VerifySourceMkmExecutable(source, expectedSha256))
            throw new UnauthorizedAccessException(
                "The installed maintenance executable is not trusted.");

        var directory = CreateDirectory(additionalExcludedRoots);
        var destination = CreateExecutablePath(directory, filePrefix);
        try
        {
            using (var input = new FileStream(
                       source,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       128 * 1024,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       destination,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.WriteThrough))
            {
                input.CopyTo(output, 128 * 1024);
                output.Flush(flushToDisk: true);
            }

            ProtectFile(destination);
            if (!VerifyMkmExecutable(destination, expectedSha256))
                throw new UnauthorizedAccessException(
                    "The staged maintenance executable failed final trust validation.");
            return new(directory, destination, expectedSha256);
        }
        catch
        {
            TryCleanupDirectory(directory, destination);
            throw;
        }
    }

    public static string CreateExecutablePath(string directory, string filePrefix)
    {
        if (!string.Equals(
                filePrefix,
                UninstallFilePrefix,
                StringComparison.Ordinal) &&
            !string.Equals(
                filePrefix,
                VcRedistFilePrefix,
                StringComparison.Ordinal))
            throw new ArgumentException("Executable staging prefix is invalid.", nameof(filePrefix));
        return Path.Combine(
            Path.GetFullPath(directory),
            filePrefix + Guid.NewGuid().ToString("N") + ".exe");
    }

    [SupportedOSPlatform("windows")]
    public static bool ProtectAndVerifyMicrosoftExecutable(
        string executablePath,
        string expectedSha256)
    {
        try
        {
            ProtectFile(executablePath);
            return VerifyMicrosoftExecutable(executablePath, expectedSha256);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or SystemException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    public static bool VerifyMicrosoftExecutable(
        string executablePath,
        string expectedSha256) =>
        ValidateExecutable(executablePath) &&
        HashMatches(executablePath, expectedSha256) &&
        AuthenticodePublisherVerifier.VerifyPublisher(
            executablePath,
            MicrosoftPublisher).IsTrusted;

    [SupportedOSPlatform("windows")]
    public static bool VerifyMkmExecutable(
        string executablePath,
        string expectedSha256) =>
        ValidateExecutable(executablePath) &&
        HashMatches(executablePath, expectedSha256) &&
        AuthenticodePublisherVerifier.Verify(executablePath).IsTrusted;

    public static bool IsApprovedStagedUninstallPath(
        string executablePath,
        string commonData,
        string tempRoot,
        params string[] excludedRoots)
    {
        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            var directory = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            return directory is not null &&
                   IsApprovedStagingDirectory(
                       directory,
                       commonData,
                       tempRoot,
                       excludedRoots) &&
                   fileName.StartsWith(
                       UninstallFilePrefix,
                       StringComparison.OrdinalIgnoreCase) &&
                   fileName.Length == UninstallFilePrefix.Length + 32 + 4 &&
                   fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                   IsHex(fileName.AsSpan(UninstallFilePrefix.Length, 32));
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static bool IsApprovedStagingDirectory(
        string candidate,
        string commonData,
        string tempRoot,
        params string[] excludedRoots)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(candidate) ||
                string.IsNullOrWhiteSpace(commonData) ||
                string.IsNullOrWhiteSpace(tempRoot) ||
                !Path.IsPathFullyQualified(candidate) ||
                !Path.IsPathFullyQualified(commonData) ||
                !Path.IsPathFullyQualified(tempRoot))
                return false;

            var fullCandidate = Trim(Path.GetFullPath(candidate));
            var fullCommonData = Trim(Path.GetFullPath(commonData));
            if (IsUnc(fullCandidate) || IsUnc(fullCommonData) ||
                !string.Equals(
                    Trim(Path.GetDirectoryName(fullCandidate) ?? string.Empty),
                    fullCommonData,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            var name = Path.GetFileName(fullCandidate);
            if (!name.StartsWith(DirectoryPrefix, StringComparison.Ordinal) ||
                name.Length != DirectoryPrefix.Length + 32 ||
                !IsHex(name.AsSpan(DirectoryPrefix.Length, 32)))
                return false;

            var forbidden = DefaultExcludedRoots(fullCommonData)
                .Append(tempRoot)
                .Concat(excludedRoots ?? []);
            return forbidden
                .Where(root => !string.IsNullOrWhiteSpace(root) &&
                               Path.IsPathFullyQualified(root))
                .All(root => !IsWithinOrEqual(fullCandidate, Path.GetFullPath(root)));
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    public static bool ValidateDirectory(string directory)
    {
        try
        {
            var commonData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (!IsApprovedStagingDirectory(
                    directory,
                    commonData,
                    Path.GetTempPath()))
                return false;
            ValidateExistingAncestors(directory);
            var info = new DirectoryInfo(directory);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            return ValidateAcl(
                info.GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner),
                directoryRules: true);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or
            SystemException or ArgumentException)
        {
            return false;
        }
    }

    public static void TryCleanupDirectory(
        string? directory,
        string? executablePath = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;
        try
        {
            var fullDirectory = Path.GetFullPath(directory);
            var commonData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (!IsApprovedStagingDirectory(
                    fullDirectory,
                    commonData,
                    Path.GetTempPath()))
                return;
            if (OperatingSystem.IsWindows() && !ValidateDirectory(fullDirectory))
                return;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                var fullExecutable = Path.GetFullPath(executablePath);
                if (string.Equals(
                        Path.GetDirectoryName(fullExecutable),
                        fullDirectory,
                        StringComparison.OrdinalIgnoreCase) &&
                    IsApprovedStagedExecutableFileName(
                        Path.GetFileName(fullExecutable)) &&
                    File.Exists(fullExecutable))
                    File.Delete(fullExecutable);
            }
            if (Directory.Exists(fullDirectory) &&
                !Directory.EnumerateFileSystemEntries(fullDirectory).Any())
                Directory.Delete(fullDirectory);
        }
        catch
        {
            // Cleanup is best effort. The protected directory remains non-user-writable.
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool VerifySourceMkmExecutable(
        string source,
        string expectedSha256)
    {
        try
        {
            if (!File.Exists(source) || IsUnc(source) ||
                File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint) ||
                !HashMatches(source, expectedSha256))
                return false;
            return AuthenticodePublisherVerifier.Verify(source).IsTrusted;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or SystemException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ValidateExecutable(string executablePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(executablePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (directory is null ||
                !IsApprovedStagingDirectory(
                    directory,
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    Path.GetTempPath()) ||
                !ValidateDirectory(directory))
                return false;

            var info = new FileInfo(fullPath);
            return info.Exists &&
                   !info.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                   ValidateAcl(
                       info.GetAccessControl(
                           AccessControlSections.Access | AccessControlSections.Owner),
                       directoryRules: false);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or
            SystemException or ArgumentException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFile(string path)
    {
        new HandleBoundAcl().ApplyBatch(
        [
            new(
                path,
                IsDirectory: false,
                StagedFilePolicy()),
        ]);
        if (!ValidateAcl(
                new FileInfo(path).GetAccessControl(
                    AccessControlSections.Access | AccessControlSections.Owner),
                directoryRules: false))
            throw new UnauthorizedAccessException("Staged executable ACL validation failed.");
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectory(string path) =>
        new HandleBoundAcl().ApplyBatch(
        [
            new(
                path,
                IsDirectory: true,
                StagedDirectoryPolicy()),
        ]);

    [SupportedOSPlatform("windows")]
    private static HandleBoundAclPolicy StagedDirectoryPolicy()
    {
        const InheritanceFlags inherited =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        return new(HandleBoundAcl.SystemSid,
        [
            new(HandleBoundAcl.SystemSid, FileSystemRights.FullControl, inherited),
            new(HandleBoundAcl.AdministratorsSid, FileSystemRights.FullControl, inherited),
        ]);
    }

    [SupportedOSPlatform("windows")]
    private static HandleBoundAclPolicy StagedFilePolicy() => new(
        HandleBoundAcl.SystemSid,
    [
        new(HandleBoundAcl.SystemSid, FileSystemRights.FullControl),
        new(HandleBoundAcl.AdministratorsSid, FileSystemRights.FullControl),
    ]);

    [SupportedOSPlatform("windows")]
    private static bool CreateDirectoryWithProtectedAcl(string path)
    {
        var descriptor = NewDirectorySecurity().GetSecurityDescriptorBinaryForm();
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = pin.AddrOfPinnedObject(),
                InheritHandle = 0,
            };
            return CreateDirectoryW(path, ref attributes);
        }
        finally
        {
            pin.Free();
            CryptographicOperations.ZeroMemory(descriptor);
        }
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity NewDirectorySecurity()
    {
        var security = new DirectorySecurity();
        var administrators = new SecurityIdentifier(AdministratorsSidValue);
        security.SetOwner(administrators);
        security.SetGroup(administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(SystemSidValue),
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            administrators,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static bool ValidateAcl(
        FileSystemSecurity security,
        bool directoryRules)
    {
        if (!security.AreAccessRulesProtected ||
            security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !string.Equals(owner.Value, SystemSidValue, StringComparison.Ordinal))
            return false;

        var expectedInheritance = directoryRules
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        var rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        return rules.Length == 2 &&
               rules.All(rule =>
                   !rule.IsInherited &&
                   rule.AccessControlType == AccessControlType.Allow &&
                   rule.FileSystemRights == FileSystemRights.FullControl &&
                   rule.InheritanceFlags == expectedInheritance &&
                   rule.PropagationFlags == PropagationFlags.None) &&
               HasSingleSid(rules, SystemSidValue) &&
               HasSingleSid(rules, AdministratorsSidValue);
    }

    private static bool HasSingleSid(
        IEnumerable<FileSystemAccessRule> rules,
        string sid) => rules.Count(rule =>
        string.Equals(rule.IdentityReference.Value, sid, StringComparison.Ordinal)) == 1;

    [SupportedOSPlatform("windows")]
    private static void ValidateExistingAncestors(string path)
    {
        var full = Path.GetFullPath(path);
        if (IsUnc(full))
            throw new UnauthorizedAccessException("UNC staging paths are forbidden.");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException(
                    "A staging path ancestor is a reparse point.");
        }
    }

    public static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool HashMatches(string path, string expectedSha256)
    {
        if (!TryDecodeSha256(expectedSha256, out var expected)) return false;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.SequentialScan);
            var actual = SHA256.HashData(stream);
            try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    private static bool TryDecodeSha256(string value, out byte[] bytes)
    {
        bytes = [];
        if (value is not { Length: 64 } || !IsHex(value.AsSpan())) return false;
        try
        {
            bytes = Convert.FromHexString(value);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static IEnumerable<string> DefaultExcludedRoots(string commonData)
    {
        yield return Path.Combine(commonData, "SuavoAgent");
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "Suavo", "Agent");
    }

    private static bool IsWithinOrEqual(string candidate, string root)
    {
        var fullCandidate = Trim(Path.GetFullPath(candidate));
        var fullRoot = Trim(Path.GetFullPath(root));
        return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullCandidate.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);

    private static bool IsApprovedStagedExecutableFileName(string fileName) =>
        IsPrefixedExecutable(fileName, UninstallFilePrefix) ||
        IsPrefixedExecutable(fileName, VcRedistFilePrefix);

    private static bool IsPrefixedExecutable(string fileName, string prefix) =>
        fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        fileName.Length == prefix.Length + 32 + 4 &&
        fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        IsHex(fileName.AsSpan(prefix.Length, 32));

    private static bool IsUnc(string path) =>
        path.StartsWith("\\\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsHex(ReadOnlySpan<char> value) =>
        value.Length > 0 && value.ToArray().All(character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr SecurityDescriptor;
        internal int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(
        string path,
        ref SecurityAttributes securityAttributes);
}
