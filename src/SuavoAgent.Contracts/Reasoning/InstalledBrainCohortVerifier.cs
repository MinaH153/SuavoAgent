using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Reasoning;

public sealed record InstalledBrainFileManifest(
    string Path,
    long SizeBytes,
    string Sha256);

public sealed record InstalledBrainCohortManifest(
    int SchemaVersion,
    string CohortId,
    string PublisherCanonical,
    BrainCohortPublisherManifest? PublisherManifest,
    string ModelFileName,
    long ModelSizeBytes,
    string ModelSha256,
    long NativePackageSizeBytes,
    string NativePackageSha256,
    IReadOnlyList<InstalledBrainFileManifest>? NativeFiles,
    string NativePackageKind = "");

public sealed record InstalledBrainCohortVerification(
    bool IsValid,
    string Code,
    InstalledBrainCohortManifest? Manifest = null,
    bool AuthorizationRefreshRequired = false,
    string? RequestedCanonical = null,
    BrainCohortPublisherManifest? RequestedManifest = null)
{
    internal static InstalledBrainCohortVerification Reject(string code) =>
        new(false, code);
}

/// <summary>
/// Re-proves one installed Brain cohort from its publisher-signed metadata and
/// retained package. The local extracted-file list is never authority: it must
/// equal an inventory independently reconstructed from the retained package.
/// </summary>
public static class InstalledBrainCohortVerifier
{
    public const int ManifestSchemaVersion = 3;
    public const int RetiredManifestSchemaVersion = 2;
    public const string ManifestFileName = "brain.manifest.json";
    public const string NativePackageFileName = "native.package.nupkg";
    public const string RetiredNativePackageFileName = "native.package.zip";
    public const int MaxManifestBytes = 4 * 1024 * 1024;
    public const long MaxNativeUncompressedBytes =
        BrainNativePackageExtractor.MaxArchiveUncompressedBytes;
    public const long MaxNativeEntryBytes = BrainNativePackageExtractor.MaxEntryBytes;
    public const int MaxNativeEntries = BrainNativePackageExtractor.MaxArchiveEntries;

    public static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
        WriteIndented = false,
    };

    public static Task<InstalledBrainCohortVerification> VerifyAsync(
        string cohortRoot,
        BrainCohortPublisherManifest requestedManifest,
        DateTimeOffset now,
        CancellationToken ct) =>
        VerifyAsync(
            cohortRoot,
            requestedManifest,
            BrainCohortContract.ProductionTrustedPublisherKeys,
            now,
            ct);

    public static async Task<InstalledBrainCohortVerification> VerifyAsync(
        string cohortRoot,
        BrainCohortPublisherManifest requestedManifest,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now,
        CancellationToken ct)
    {
        try
        {
            var requested = ValidateAuthorizationForInstalledCohort(
                requestedManifest,
                trustedPublisherKeys,
                now);
            if (!requested.IsValid || requested.Canonical is null)
                return InstalledBrainCohortVerification.Reject(requested.Code);
            // Setup verifies the private same-volume staging directory before
            // its atomic rename, so the directory basename may temporarily be
            // "<cohort>.staging-<nonce>". The signed manifest + caller-selected
            // path remain authoritative; never infer identity from the name.
            if (!Directory.Exists(cohortRoot) || IsReparse(cohortRoot))
                return InstalledBrainCohortVerification.Reject("cohort_root_invalid");
            if (OperatingSystem.IsWindows())
            {
                var acl = BrainCohortAcl.Verify(cohortRoot);
                if (!acl.IsValid)
                    return InstalledBrainCohortVerification.Reject(acl.Code);
            }

            var manifestPath = Path.Combine(cohortRoot, ManifestFileName);
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || IsReparse(manifestPath) ||
                manifestInfo.Length is <= 0 or > MaxManifestBytes)
                return InstalledBrainCohortVerification.Reject("local_manifest_invalid");
            var json = await File.ReadAllTextAsync(manifestPath, ct);
            if (Encoding.UTF8.GetByteCount(json) > MaxManifestBytes)
                return InstalledBrainCohortVerification.Reject("local_manifest_invalid");
            var manifest = JsonSerializer.Deserialize<InstalledBrainCohortManifest>(
                json,
                ManifestJson);
            var currentLayout = manifest is not null &&
                                manifest.SchemaVersion == ManifestSchemaVersion &&
                                requestedManifest.SchemaVersion == BrainCohortContract.SchemaVersion &&
                                string.Equals(
                                    manifest.NativePackageKind,
                                    requestedManifest.NativePackageKind,
                                    StringComparison.Ordinal) &&
                                string.Equals(
                                    manifest.NativePackageKind,
                                    BrainNativePackageExtractor.OfficialNuGetPackageKind,
                                    StringComparison.Ordinal);
            var retiredLayout = manifest is not null &&
                                manifest.SchemaVersion == RetiredManifestSchemaVersion &&
                                requestedManifest.SchemaVersion ==
                                BrainCohortContract.RetiredInstalledSchemaVersion &&
                                string.IsNullOrEmpty(manifest.NativePackageKind) &&
                                string.IsNullOrEmpty(requestedManifest.NativePackageKind);
            if (manifest is null || (!currentLayout && !retiredLayout) ||
                manifest.CohortId != requestedManifest.CohortId ||
                manifest.ModelFileName != BrainCohortContract.SafeFileNameFromUrl(
                    requestedManifest.ModelUrl,
                    "model.gguf") ||
                manifest.ModelSizeBytes != requestedManifest.ModelSizeBytes ||
                manifest.NativePackageSizeBytes != requestedManifest.NativeLibsSizeBytes ||
                !FixedHashEquals(manifest.ModelSha256, requestedManifest.ModelSha256) ||
                !FixedHashEquals(
                    manifest.NativePackageSha256,
                    requestedManifest.NativeLibsSha256) ||
                manifest.NativeFiles is not { Count: > 0 and <= MaxNativeEntries })
                return InstalledBrainCohortVerification.Reject("local_manifest_binding_invalid");

            var modelRoot = Path.Combine(cohortRoot, "model");
            var modelPath = Path.Combine(modelRoot, manifest.ModelFileName);
            var packageFileName = currentLayout
                ? NativePackageFileName
                : RetiredNativePackageFileName;
            var packagePath = Path.Combine(cohortRoot, packageFileName);
            if (!Directory.Exists(modelRoot) || IsReparse(modelRoot) ||
                Directory.EnumerateFiles(modelRoot, "*", SearchOption.TopDirectoryOnly).Count() != 1 ||
                Directory.EnumerateDirectories(modelRoot, "*", SearchOption.TopDirectoryOnly).Any() ||
                !await VerifyFileAsync(
                    modelPath,
                    requestedManifest.ModelSizeBytes,
                    requestedManifest.ModelSha256,
                    BrainCohortContract.MaxModelBytes,
                    ct) ||
                !await VerifyFileAsync(
                    packagePath,
                    requestedManifest.NativeLibsSizeBytes,
                    requestedManifest.NativeLibsSha256,
                    BrainCohortContract.MaxNativePackageBytes,
                    ct))
                return InstalledBrainCohortVerification.Reject("signed_artifact_mismatch");

            var package = currentLayout
                ? await BrainNativePackageExtractor.InspectAsync(
                    packagePath,
                    requestedManifest.NativePackageKind,
                    ct)
                : await BrainNativePackageExtractor.InspectLegacyFlatAsync(packagePath, ct);
            var packageFiles = package.NativeFiles;
            if (!package.IsValid || packageFiles is null ||
                !manifest.NativeFiles.SequenceEqual(packageFiles))
                return InstalledBrainCohortVerification.Reject("native_inventory_mismatch");

            var nativeRoot = Path.Combine(cohortRoot, "native");
            if (!Directory.Exists(nativeRoot) || IsReparse(nativeRoot))
                return InstalledBrainCohortVerification.Reject("native_root_invalid");
            var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in packageFiles)
            {
                var relative = NormalizeEntryPath(file.Path);
                if (relative is null || !expectedPaths.Add(relative) ||
                    !await VerifyFileAsync(
                        SafeEntryPath(nativeRoot, relative),
                        file.SizeBytes,
                        file.Sha256,
                        MaxNativeEntryBytes,
                        ct))
                    return InstalledBrainCohortVerification.Reject("native_file_mismatch");
            }
            var actualPaths = EnumerateTreeFilesWithoutReparse(nativeRoot, MaxNativeEntries);
            if (actualPaths is null || actualPaths.Count != expectedPaths.Count ||
                actualPaths.Any(path => !expectedPaths.Contains(path)))
                return InstalledBrainCohortVerification.Reject("native_tree_mismatch");

            var allowedTopLevel = new HashSet<string>(StringComparer.Ordinal)
            {
                "model", "native", ManifestFileName, packageFileName,
            };
            if (Directory.EnumerateFileSystemEntries(cohortRoot)
                .Select(Path.GetFileName)
                .Any(name => name is null || !allowedTopLevel.Contains(name)))
                return InstalledBrainCohortVerification.Reject("cohort_extra_entry");

            var persisted = ValidateAuthorizationForInstalledCohort(
                manifest.PublisherManifest,
                trustedPublisherKeys,
                now);
            var refresh = !persisted.IsValid ||
                          manifest.PublisherManifest?.SchemaVersion !=
                          requestedManifest.SchemaVersion ||
                          !string.Equals(
                              manifest.PublisherCanonical,
                              requested.Canonical,
                              StringComparison.Ordinal) ||
                          !string.Equals(
                              manifest.PublisherManifest?.ModelSignature,
                              requestedManifest.ModelSignature,
                              StringComparison.Ordinal) ||
                          !string.Equals(
                              manifest.PublisherManifest?.NativeSignature,
                              requestedManifest.NativeSignature,
                              StringComparison.Ordinal);
            return new(
                true,
                "valid",
                manifest,
                refresh,
                requested.Canonical,
                requestedManifest);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or ArgumentException or CryptographicException or NotSupportedException)
        {
            return InstalledBrainCohortVerification.Reject("cohort_verification_failed");
        }
    }

    public static InstalledBrainCohortManifest RenewAuthorization(
        InstalledBrainCohortVerification verified,
        BrainCohortPublisherManifest requestedManifest)
    {
        if (!verified.IsValid || verified.Manifest is null ||
            string.IsNullOrEmpty(verified.RequestedCanonical) ||
            verified.RequestedManifest is null ||
            !Equals(verified.RequestedManifest, requestedManifest) ||
            verified.Manifest.SchemaVersion !=
            (requestedManifest.SchemaVersion == BrainCohortContract.SchemaVersion
                ? ManifestSchemaVersion
                : RetiredManifestSchemaVersion) ||
            !string.Equals(
                BrainCohortContract.BuildCanonical(requestedManifest),
                verified.RequestedCanonical,
                StringComparison.Ordinal) ||
            requestedManifest.CohortId != verified.Manifest.CohortId)
            throw new InvalidOperationException("Only a fully verified cohort can be renewed.");
        return verified.Manifest with
        {
            PublisherCanonical = verified.RequestedCanonical,
            PublisherManifest = requestedManifest,
        };
    }

    /// <summary>
    /// Authorization gate for runtime re-verification only. Schema v3 follows
    /// the active contract; retired schema v2 can pass solely so an existing
    /// on-disk cohort can be re-proved. Provisioners and selectors must use
    /// <see cref="BrainCohortContract.Validate(BrainCohortPublisherManifest?,IReadOnlyDictionary{string,string},DateTimeOffset)"/>.
    /// </summary>
    public static BrainCohortValidationResult ValidateAuthorizationForInstalledCohort(
        BrainCohortPublisherManifest? manifest,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now) =>
        manifest?.SchemaVersion == BrainCohortContract.RetiredInstalledSchemaVersion
            ? BrainCohortContract.ValidateRetiredSchemaV2InstalledCohort(
                manifest,
                trustedPublisherKeys,
                now)
            : BrainCohortContract.Validate(manifest, trustedPublisherKeys, now);

    private static async Task<bool> VerifyFileAsync(
        string path,
        long expectedBytes,
        string expectedSha,
        long maxBytes,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || IsReparse(path) || expectedBytes <= 0 ||
            expectedBytes > maxBytes || info.Length != expectedBytes)
            return false;
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct))
            .ToLowerInvariant();
        return FixedHashEquals(actual, expectedSha);
    }

    private static string? NormalizeEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 || value.Any(char.IsControl))
            return null;
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || normalized.StartsWith('/') ||
            normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            return null;
        return normalized;
    }

    private static string SafeEntryPath(string root, string relative)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(
            canonicalRoot,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new InvalidDataException("Native package entry escaped its cohort.");
        return path;
    }

    private static IReadOnlyList<string>? EnumerateTreeFilesWithoutReparse(
        string root,
        int maxEntries)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        var count = 0;
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > maxEntries || IsReparse(entry)) return null;
                if (Directory.Exists(entry)) pending.Push(entry);
                else files.Add(Path.GetRelativePath(root, entry)
                    .Replace(Path.DirectorySeparatorChar, '/'));
            }
        }
        return files;
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool FixedHashEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
}
