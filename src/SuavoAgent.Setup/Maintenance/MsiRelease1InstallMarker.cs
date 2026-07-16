using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.InstallerSupport;

namespace SuavoAgent.Setup.Maintenance;

internal enum MsiRelease1InstallMarkerExitCode
{
    Success = 0,
    InvalidArguments = 60,
    UnsupportedHost = 61,
    MarkerWriteFailed = 62,
    BootIdentityUnavailable = 63,
    UntrustedProcessIdentity = 64,
    PrepareFailed = 65,
    RollbackFailed = 66,
    CommitCleanupFailed = 67,
}

/// <summary>
/// Commit-only MSI proof that a complete first-install transaction reached
/// Windows Installer commit. Repair keeps service hardening but cannot mint a
/// new full-reinstall marker. Pairing consumes the marker once and signs the
/// resulting release receipt with the machine's maintenance TPM key.
/// </summary>
internal static class MsiRelease1InstallMarkerRunner
{
    internal const string Switch = "--msi-write-release-install-marker";
    internal const string RollbackSwitch = "--msi-rollback-release-install-marker";
    internal const string CommitSwitch = "--msi-commit-release-install-marker";
    internal static readonly IReadOnlyList<string> Switches =
    [
        RollbackSwitch,
        Switch,
        CommitSwitch,
    ];
    internal static bool IsRequested(IReadOnlyList<string>? arguments) =>
        arguments?.Any(argument => Switches.Any(candidate => string.Equals(
            argument,
            candidate,
            StringComparison.OrdinalIgnoreCase))) == true;

    internal static int Run(IReadOnlyList<string>? arguments) => Run(
        arguments,
        OperatingSystem.IsWindows(),
        IsCurrentProcessLocalSystem,
        Release1MsiInstallMarkerTransaction.RollbackForInstalledHost,
        Release1MsiInstallMarkerTransaction.PrepareAndWriteForInstalledHost,
        Release1MsiInstallMarkerTransaction.CommitForInstalledHost);

    internal static int Run(
        IReadOnlyList<string>? arguments,
        bool isWindows,
        Func<bool> isLocalSystem,
        Action<string, string, string, string> writeMarker)
        => Run(
            arguments,
            isWindows,
            isLocalSystem,
            static (_, _) => throw new InvalidOperationException(),
            writeMarker,
            static (_, _) => throw new InvalidOperationException());

    internal static int Run(
        IReadOnlyList<string>? arguments,
        bool isWindows,
        Func<bool> isLocalSystem,
        Action<string, string> rollback,
        Action<string, string, string, string> writeMarker,
        Action<string, string> commit)
    {
        if (arguments is null ||
            arguments.Count != 2 ||
            !Switches.Any(candidate => string.Equals(
                arguments[0],
                candidate,
                StringComparison.OrdinalIgnoreCase)) ||
            !MsiInstallerInvocation.TryParse(arguments[1], out var invocation))
            return (int)MsiRelease1InstallMarkerExitCode.InvalidArguments;
        var requestedSwitch = arguments[0];
        if (!isWindows)
            return (int)MsiRelease1InstallMarkerExitCode.UnsupportedHost;

        ArgumentNullException.ThrowIfNull(isLocalSystem);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(writeMarker);
        ArgumentNullException.ThrowIfNull(commit);
        try
        {
            if (!isLocalSystem())
                return (int)MsiRelease1InstallMarkerExitCode.UntrustedProcessIdentity;
        }
        catch
        {
            // Identity resolution is fail-closed. No token, SID, account name,
            // or exception text is allowed to cross the MSI boundary.
            return (int)MsiRelease1InstallMarkerExitCode.UntrustedProcessIdentity;
        }
        try
        {
            if (string.Equals(
                         requestedSwitch,
                         RollbackSwitch,
                         StringComparison.OrdinalIgnoreCase))
                rollback(invocation.InstallDirectory, invocation.InvocationId);
            else if (string.Equals(
                         requestedSwitch,
                         CommitSwitch,
                         StringComparison.OrdinalIgnoreCase))
                commit(invocation.InstallDirectory, invocation.InvocationId);
            else
                writeMarker(
                    invocation.OriginalDatabase,
                    invocation.ProductCode,
                    invocation.InstallDirectory,
                    invocation.InvocationId);
            return (int)MsiRelease1InstallMarkerExitCode.Success;
        }
        catch (Release1BootIdentityUnavailableException)
        {
            return (int)MsiRelease1InstallMarkerExitCode.BootIdentityUnavailable;
        }
        catch
        {
            // The MSI log receives only a bounded code. No workstation path,
            // identity, or exception text crosses this boundary.
            return string.Equals(
                    requestedSwitch,
                    RollbackSwitch,
                    StringComparison.OrdinalIgnoreCase)
                    ? (int)MsiRelease1InstallMarkerExitCode.RollbackFailed
                : string.Equals(
                        requestedSwitch,
                        CommitSwitch,
                        StringComparison.OrdinalIgnoreCase)
                        ? (int)MsiRelease1InstallMarkerExitCode.CommitCleanupFailed
                        : (int)MsiRelease1InstallMarkerExitCode.MarkerWriteFailed;
        }
    }

    private static bool IsCurrentProcessLocalSystem()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        }
        catch { return false; }
    }

}

internal static class Release1MsiInstallMarkerStore
{
    private const int MaxInstallStateBytes = 64 * 1024;
    private const int MaxMarkerBytes = 64 * 1024;
    private const int MaxProofRootEntries = 32;
    private const long MaxInstallerBytes = 2L * 1024 * 1024 * 1024;

    internal static string DefaultProofDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Release1ConvergenceContract.InstallProofRootDirectoryName);

    internal static void WriteForInstalledHostUnderProofLock(
        string installDirectory,
        string originalDatabase,
        string productCode,
        string transactionId)
    {
        var fixedInstallDirectory =
            MsiInstallerInvocation.RequireFixedInstallDirectory(
                installDirectory);
        Write(
            fixedInstallDirectory,
            DefaultProofDirectory(),
            DateTimeOffset.UtcNow,
            Release1ConvergenceContract.CurrentBootToken(),
            transactionId,
            originalDatabase,
            productCode,
            AuthenticodePublisherVerifier.Verify,
            proofLockHeld: true);
    }

    internal static string Write(
        string installDirectory,
        string proofDirectory,
        DateTimeOffset completedAtUtc,
        string bootToken,
        string transactionId,
        string originalDatabase,
        string productCode,
        Func<string, AuthenticodePublisherTrust> verifyInstallerAuthenticode,
        bool proofLockHeld = false)
    {
        var installRoot = SafeDirectory(installDirectory);
        var proofRoot = proofLockHeld
            ? RequireProtectedProofDirectory(proofDirectory)
            : CreateAndProtectProofDirectory(proofDirectory);
        if (!LowerHex64(transactionId))
            throw new InvalidDataException("MSI transaction identity is invalid.");
        ArgumentNullException.ThrowIfNull(verifyInstallerAuthenticode);

        var normalizedProductCode = NormalizeProductCode(productCode);
        var installerPath = SafeInstallerPath(originalDatabase);
        var installerArtifactSha256 = Sha256File(installerPath);
        var installerTrust = verifyInstallerAuthenticode(installerPath);
        if (!installerTrust.IsTrusted ||
            !string.Equals(
                installerTrust.Publisher,
                AuthenticodePublisherVerifier.ExpectedPublisher,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The MSI installer publisher identity is not approved.");
        EnsureRegularBoundedFile(installerPath, MaxInstallerBytes);
        if (!string.Equals(
                installerArtifactSha256,
                Sha256File(installerPath),
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The MSI installer changed while its identity was measured.");

        var maintenancePath = Path.Combine(
            installRoot,
            MaintenanceContract.ExecutableName);
        var statePath = Path.Combine(
            installRoot,
            MaintenanceContract.InstallStateFileName);
        EnsureRegularBoundedFile(maintenancePath, 2L * 1024 * 1024 * 1024);
        EnsureRegularBoundedFile(statePath, MaxInstallStateBytes);

        using var state = JsonDocument.Parse(
            File.ReadAllBytes(statePath),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        if (state.RootElement.ValueKind != JsonValueKind.Object ||
            !state.RootElement.TryGetProperty("version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Installed release identity is missing.");
        var version = versionElement.GetString();
        if (!BinaryDownloader.IsValidReleaseTag(version))
            throw new InvalidDataException("Installed release identity is invalid.");

        var marker = new Release1MsiInstallCommitMarker(
            SchemaVersion:
                Release1ConvergenceContract.MsiInstallCommitMarkerSchemaVersion,
            Purpose: Release1ConvergenceContract.MsiInstallCommitMarkerPurpose,
            InstalledReleaseTag: NormalizeReleaseTag(version!),
            MaintenanceHostSha256: Sha256File(maintenancePath),
            InstallerArtifactSha256: installerArtifactSha256,
            ProductCode: normalizedProductCode,
            InstallTransactionId: transactionId.ToLowerInvariant(),
            InstallCompletedAtUtc: Release1ConvergenceContract.ExactUtc(completedAtUtc),
            BootTokenAtInstall: ValidateBootToken(bootToken));
        var bytes = Release1ConvergenceContract.CanonicalBytes(marker);
        if (bytes.Length is <= 0 or > MaxMarkerBytes)
            throw new InvalidDataException("MSI install marker exceeds its bound.");

        var path = Path.Combine(
            proofRoot,
            Release1ConvergenceContract.MsiInstallCommitMarkerFileName);
        WriteAtomic(path, bytes);
        EnsureRegularBoundedFile(path, MaxMarkerBytes);
        if (proofLockHeld)
            ProtectAndVerifyProofFile(proofRoot, path, MaxMarkerBytes);
        else
            ProtectProofDirectory(proofRoot);
        VerifyProtectedMarker(proofRoot, path);
        return path;
    }

    internal static Release1MsiInstallCommitMarker Read(string proofDirectory)
    {
        var root = SafeDirectory(proofDirectory);
        var path = Path.Combine(
            root,
            Release1ConvergenceContract.MsiInstallCommitMarkerFileName);
        VerifyProtectedMarker(root, path);
        EnsureRegularBoundedFile(path, MaxMarkerBytes);
        var marker = JsonSerializer.Deserialize<Release1MsiInstallCommitMarker>(
            File.ReadAllBytes(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("MSI install marker is empty.");
        Validate(marker);
        return marker;
    }

    internal static bool Exists(string proofDirectory) => File.Exists(Path.Combine(
        SafeDirectory(proofDirectory),
        Release1ConvergenceContract.MsiInstallCommitMarkerFileName));

    internal static void Consume(string proofDirectory, string expectedTransactionId)
    {
        var root = SafeDirectory(proofDirectory);
        var marker = Read(root);
        if (!string.Equals(
                marker.InstallTransactionId,
                expectedTransactionId,
                StringComparison.Ordinal))
            throw new InvalidDataException("MSI install marker changed before consumption.");
        var path = Path.Combine(
            root,
            Release1ConvergenceContract.MsiInstallCommitMarkerFileName);
        VerifyProtectedMarker(root, path);
        File.Delete(path);
    }

    internal static void Validate(Release1MsiInstallCommitMarker marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        if (marker.SchemaVersion !=
                Release1ConvergenceContract.MsiInstallCommitMarkerSchemaVersion ||
            marker.Purpose != Release1ConvergenceContract.MsiInstallCommitMarkerPurpose ||
            !BinaryDownloader.IsValidReleaseTag(marker.InstalledReleaseTag) ||
            !LowerHex64(marker.MaintenanceHostSha256) ||
            !LowerHex64(marker.InstallerArtifactSha256) ||
            !IsCanonicalProductCode(marker.ProductCode) ||
            !LowerHex64(marker.InstallTransactionId) ||
            !DateTimeOffset.TryParseExact(
                marker.InstallCompletedAtUtc,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out _) ||
            ValidateBootToken(marker.BootTokenAtInstall) != marker.BootTokenAtInstall)
            throw new InvalidDataException("MSI install marker is invalid.");
    }

    private static string SafeInstallerPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException("MSI installer path is invalid.");
        var full = Path.GetFullPath(value);
        if (full.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetExtension(full),
                ".msi",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("MSI installer must be a local MSI file.");
        if (OperatingSystem.IsWindows())
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root) ||
                new DriveInfo(root).DriveType == DriveType.Network)
                throw new InvalidDataException(
                    "MSI installer must be on a local filesystem.");
            EnsureNoReparsePoints(full);
        }
        EnsureRegularBoundedFile(full, MaxInstallerBytes);
        return full;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidDataException("MSI installer has no filesystem root.");
        var current = root;
        foreach (var segment in full[root.Length..].Split(
                     new[]
                     {
                         Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar,
                     },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                throw new InvalidDataException("MSI installer is unavailable.");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "MSI installer path must not contain reparse points.");
        }
    }

    private static string NormalizeProductCode(string? value)
    {
        if (!Guid.TryParseExact(value, "B", out var parsed))
            throw new InvalidDataException("MSI product identity is invalid.");
        return parsed.ToString("B").ToUpperInvariant();
    }

    private static bool IsCanonicalProductCode(string? value) =>
        Guid.TryParseExact(value, "B", out var parsed) &&
        string.Equals(
            value,
            parsed.ToString("B").ToUpperInvariant(),
            StringComparison.Ordinal);

    private static string ValidateBootToken(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 96 ||
            value.Any(character => character is < ' ' or > '~' or '|'))
            throw new InvalidDataException("MSI boot token is invalid.");
        return value;
    }

    private static string NormalizeReleaseTag(string value) =>
        value.StartsWith('v') || value.StartsWith('V')
            ? "v" + value[1..]
            : "v" + value;

    internal static HandleBoundAclPolicy ProofDirectoryPolicy(bool inherit)
    {
        var inheritance = inherit
            ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
            : InheritanceFlags.None;
        return new(
            HandleBoundAcl.SystemSid,
            [
                new(
                    HandleBoundAcl.SystemSid,
                    FileSystemRights.FullControl,
                    inheritance),
                new(
                    HandleBoundAcl.AdministratorsSid,
                    FileSystemRights.FullControl,
                    inheritance),
            ]);
    }

    internal static HandleBoundAclPolicy ProofFilePolicy() => new(
        HandleBoundAcl.SystemSid,
        [
            new(HandleBoundAcl.SystemSid, FileSystemRights.FullControl),
            new(HandleBoundAcl.AdministratorsSid, FileSystemRights.FullControl),
        ]);

    internal static string CreateAndProtectProofDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException("MSI proof directory is invalid.");
        var full = Path.GetFullPath(value);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("MSI proof directory must be local.");
        Directory.CreateDirectory(full);
        var root = SafeDirectory(full);
        ProtectProofDirectory(root);
        return root;
    }

    internal static string RequireProtectedProofDirectory(string value)
    {
        var root = SafeDirectory(value);
        VerifyProtectedProofObjects(root, [], [], MaxMarkerBytes);
        return root;
    }

    internal static void ProtectProofDirectory(string root)
    {
        if (Release1MsiInstallMarkerTransaction.IsProofLockHeldByCurrentContext)
            throw new InvalidOperationException(
                "Recursive proof protection is forbidden while the MSI proof lock is held.");
        if (!OperatingSystem.IsWindows()) return;
        new HandleBoundAcl().ApplyTree(
            root,
            ProofDirectoryPolicy(inherit: true),
            ProofFilePolicy(),
            ProofDirectoryPolicy(inherit: false),
            maximumEntries: MaxProofRootEntries,
            maximumDepth: 4);
    }

    /// <summary>
    /// Applies and verifies the exact file ACL without recursively traversing
    /// the proof root. This is the only protection path allowed while the
    /// transaction lock is held.
    /// </summary>
    internal static void ProtectAndVerifyProofFile(
        string proofRoot,
        string filePath,
        long maximumFileBytes)
    {
        var root = RequireProtectedProofDirectory(proofRoot);
        var path = Path.GetFullPath(filePath);
        if (!IsStrictChild(root, path))
            throw new InvalidDataException(
                "MSI proof object escaped its protected root.");
        EnsureRegularBoundedFile(path, maximumFileBytes);
        if (OperatingSystem.IsWindows())
        {
            new HandleBoundAcl().ApplyBatch(
            [
                new HandleBoundAclMutation(
                    path,
                    IsDirectory: false,
                    ProofFilePolicy()),
            ]);
        }
        VerifyProtectedProofObjects(root, [], [path], maximumFileBytes);
    }

    private static void VerifyProtectedMarker(string root, string markerPath)
    {
        VerifyProtectedProofObjects(root, [], [markerPath], MaxMarkerBytes);
    }

    internal static void VerifyProtectedProofObjects(
        string proofRoot,
        IEnumerable<string> directories,
        IEnumerable<string> files,
        long maximumFileBytes)
    {
        if (maximumFileBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        var root = SafeDirectory(proofRoot);
        var directoryPaths = directories
            .Select(SafeDirectory)
            .Distinct(PathComparer())
            .ToArray();
        var filePaths = files
            .Select(Path.GetFullPath)
            .Distinct(PathComparer())
            .ToArray();
        if (directoryPaths.Any(path => !IsSameOrStrictChild(root, path)) ||
            filePaths.Any(path => !IsStrictChild(root, path)))
            throw new InvalidDataException("MSI proof object escaped its protected root.");
        foreach (var path in filePaths)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidDataException("MSI proof object must be local.");
            EnsureRegularBoundedFile(path, maximumFileBytes);
        }
        if (!OperatingSystem.IsWindows()) return;

        var mutations = new List<HandleBoundAclMutation>(
            1 + directoryPaths.Length + filePaths.Length)
        {
            new(root, IsDirectory: true, ProofDirectoryPolicy(inherit: true)),
        };
        mutations.AddRange(directoryPaths
            .Where(path => !PathComparer().Equals(path, root))
            .Select(path => new HandleBoundAclMutation(
                path,
                IsDirectory: true,
                ProofDirectoryPolicy(inherit: true))));
        mutations.AddRange(filePaths.Select(path => new HandleBoundAclMutation(
            path,
            IsDirectory: false,
            ProofFilePolicy())));
        new HandleBoundAcl().VerifyBatch(mutations);
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool IsSameOrStrictChild(string root, string candidate) =>
        PathComparer().Equals(root, candidate) || IsStrictChild(root, candidate);

    private static bool IsStrictChild(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !string.Equals(relative, ".", StringComparison.Ordinal) &&
               !Path.IsPathFullyQualified(relative) &&
               relative != ".." &&
               !relative.StartsWith(
                   ".." + Path.DirectorySeparatorChar,
                   StringComparison.Ordinal) &&
               !relative.StartsWith(
                   ".." + Path.AltDirectorySeparatorChar,
                   StringComparison.Ordinal);
    }

    private static string SafeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException("MSI proof directory is invalid.");
        var full = Path.GetFullPath(value);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("MSI proof directory must be local.");
        var directory = new DirectoryInfo(full);
        if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("MSI proof directory is unavailable.");
        return directory.FullName.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static void EnsureRegularBoundedFile(string path, long maximumBytes)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > maximumBytes ||
            file.Attributes.HasFlag(FileAttributes.Directory) ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("MSI proof artifact is untrusted.");
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
