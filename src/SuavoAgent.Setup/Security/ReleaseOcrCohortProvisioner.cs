using System.IO.Compression;
using System.Security.Cryptography;
using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Setup.Security;

internal sealed record ReleaseOcrProvisionResult(
    bool Succeeded,
    string Code,
    string? CohortId = null);

/// <summary>
/// Privileged owner of native OCR bytes. Core never downloads, extracts,
/// replaces, or changes ACLs on executable cohorts. Setup validates the exact
/// release catalog in memory, creates only the content-addressed destination,
/// and invokes the handle-bound ACL boundary before and after writing files.
/// </summary>
internal static class ReleaseOcrCohortProvisioner
{
    private const int BufferSize = 128 * 1024;

    internal static async Task<ReleaseOcrProvisionResult> ProvisionAllAsync(
        string dataDirectory,
        Func<string, bool> lockdownCohortAcl,
        CancellationToken cancellationToken,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(lockdownCohortAcl);
        if (string.IsNullOrWhiteSpace(dataDirectory))
            return new(false, "vision_cohort_data_directory_invalid");

        using var http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromMinutes(20);
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "SuavoAgent-Setup/1.0 (+https://suavollc.com)");

        var visionRoot = VisionRoot(dataDirectory);
        try
        {
            Directory.CreateDirectory(visionRoot);
            if (!lockdownCohortAcl(visionRoot))
                return new(false, "vision_release_root_acl_failed");
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or ArgumentException or
                   NotSupportedException)
        {
            return new(false, $"vision_release_root_exception_{exception.GetType().Name}");
        }

        foreach (var cohort in ReleaseOcrCohortCatalog.Approved)
        {
            var result = await ProvisionOneAsync(
                    dataDirectory,
                    cohort,
                    lockdownCohortAcl,
                    http,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded) return result;
        }

        // Protect the highest replaceable ancestor, not only the hash leaf.
        // The data root grants Core Modify for runtime state; locking `vision`
        // removes Core Delete on this child so it cannot swap the verified
        // cohort between Helper's hash proof and LoadLibraryEx.
        if (!ReassertInstalledCohortAcls(dataDirectory, lockdownCohortAcl))
            return new(false, "vision_release_root_final_acl_failed");

        return new(true, "vision_release_cohorts_ready");
    }

    internal static async Task<ReleaseOcrProvisionResult> ProvisionOneForTestsAsync(
        string dataDirectory,
        ReleaseOcrCohort cohort,
        Func<string, bool> lockdownCohortAcl,
        HttpMessageHandler handler,
        CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(1),
        };
        return await ProvisionOneAsync(
                dataDirectory,
                cohort,
                lockdownCohortAcl,
                http,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static bool ReassertInstalledCohortAcls(
        string dataDirectory,
        Func<string, bool> lockdownCohortAcl) =>
        ReassertInstalledCohortAcls(
            dataDirectory,
            lockdownCohortAcl,
            ReleaseOcrCohortCatalog.Approved);

    internal static bool ReassertInstalledCohortAclsForTests(
        string dataDirectory,
        Func<string, bool> lockdownCohortAcl,
        IReadOnlyList<ReleaseOcrCohort> cohorts) =>
        ReassertInstalledCohortAcls(dataDirectory, lockdownCohortAcl, cohorts);

    private static bool ReassertInstalledCohortAcls(
        string dataDirectory,
        Func<string, bool> lockdownCohortAcl,
        IReadOnlyList<ReleaseOcrCohort> cohorts)
    {
        ArgumentNullException.ThrowIfNull(lockdownCohortAcl);
        try
        {
            var cohortsRoot = CohortsRoot(dataDirectory);
            foreach (var cohort in cohorts)
            {
                var target = Path.Combine(cohortsRoot, cohort.BundleSha256);
                if (!ReleaseOcrCohortCatalog.VerifyInstalledAt(
                        target,
                        cohortsRoot,
                        cohort))
                    return false;
            }
            if (!lockdownCohortAcl(VisionRoot(dataDirectory))) return false;
            foreach (var cohort in cohorts)
            {
                if (!ReleaseOcrCohortCatalog.VerifyInstalledAt(
                        Path.Combine(cohortsRoot, cohort.BundleSha256),
                        cohortsRoot,
                        cohort))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or ArgumentException or
                   NotSupportedException or CryptographicException)
        {
            return false;
        }
    }

    private static async Task<ReleaseOcrProvisionResult> ProvisionOneAsync(
        string dataDirectory,
        ReleaseOcrCohort cohort,
        Func<string, bool> lockdownCohortAcl,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        if (!ReleaseOcrCohortCatalog.IsWellFormed(cohort))
            return new(false, "vision_release_cohort_descriptor_invalid");

        var cohortsRoot = CohortsRoot(dataDirectory);
        var target = Path.Combine(cohortsRoot, cohort.BundleSha256);
        try
        {
            EnsureExistingPathIsRegularDirectory(dataDirectory);
            EnsureDirectoryChain(dataDirectory, Path.Combine(dataDirectory, "vision"));
            EnsureDirectoryChain(dataDirectory, cohortsRoot);

            if (Directory.Exists(target) &&
                ReleaseOcrCohortCatalog.VerifyInstalledAt(target, cohortsRoot, cohort))
            {
                if (!lockdownCohortAcl(target) ||
                    !ReleaseOcrCohortCatalog.VerifyInstalledAt(target, cohortsRoot, cohort))
                    return new(false, "vision_release_cohort_acl_failed", cohort.CohortId);
                return new(true, "vision_release_cohort_already_provisioned", cohort.CohortId);
            }

            // Validate every remote byte before creating or replacing the live
            // executable directory. The approved cohort is small enough to
            // keep this bounded validation entirely in memory.
            var package = await DownloadExactAsync(
                    http,
                    cohort.BundleUrl,
                    cohort.BundleSizeBytes,
                    cohort.BundleSha256,
                    ReleaseOcrCohortCatalog.MaxBundleBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (package is null)
                return new(false, "vision_release_cohort_bundle_mismatch", cohort.CohortId);

            var inventory = await ExtractExactInventoryAsync(
                    http,
                    package,
                    cohort,
                    cancellationToken)
                .ConfigureAwait(false);
            if (inventory is null)
                return new(false, "vision_release_cohort_inventory_mismatch", cohort.CohortId);

            if (Directory.Exists(target))
            {
                // The ACL callback is also the no-follow/hardlink proof. Never
                // recursively delete a pre-existing tree that failed it.
                if (!lockdownCohortAcl(target))
                    return new(false, "vision_release_cohort_existing_tree_untrusted", cohort.CohortId);
                Directory.Delete(target, recursive: true);
            }
            else if (File.Exists(target))
            {
                return new(false, "vision_release_cohort_target_not_directory", cohort.CohortId);
            }

            Directory.CreateDirectory(target);
            if (!lockdownCohortAcl(target))
                return new(false, "vision_release_cohort_initial_acl_failed", cohort.CohortId);

            foreach (var item in inventory.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var path = ReleaseOcrCohortCatalog.SafeEntryPath(target, item.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    BufferSize,
                    FileOptions.WriteThrough);
                stream.Write(item.Value);
                stream.Flush(flushToDisk: true);
            }

            var manifestPath = Path.Combine(
                target,
                ReleaseOcrCohortCatalog.ManifestFileName);
            var manifest = ReleaseOcrCohortCatalog.SerializeManifest(cohort);
            using (var stream = new FileStream(
                       manifestPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       BufferSize,
                       FileOptions.WriteThrough))
            {
                stream.Write(manifest);
                stream.Flush(flushToDisk: true);
            }

            if (!lockdownCohortAcl(target) ||
                !ReleaseOcrCohortCatalog.VerifyInstalledAt(target, cohortsRoot, cohort))
                return new(false, "vision_release_cohort_final_verification_failed", cohort.CohortId);
            return new(true, "vision_release_cohort_provisioned", cohort.CohortId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or InvalidDataException or
                   HttpRequestException or CryptographicException or ArgumentException or
                   NotSupportedException or OverflowException)
        {
            SetupLog.Append(
                "ERROR",
                $"vision_release_cohort_provision_failed error_type={exception.GetType().Name}");
            return new(
                false,
                $"vision_release_cohort_exception_{exception.GetType().Name}",
                cohort.CohortId);
        }
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>?>
        ExtractExactInventoryAsync(
            HttpClient http,
            byte[] package,
            ReleaseOcrCohort cohort,
            CancellationToken cancellationToken)
    {
        var remoteDestination = cohort.TrainedDataSource is null
            ? null
            : ReleaseOcrCohortCatalog.NormalizeRelativePath(
                cohort.TrainedDataSource.DestinationPath);
        var expected = cohort.Files
            .Where(file => !string.Equals(
                ReleaseOcrCohortCatalog.NormalizeRelativePath(file.Path),
                remoteDestination,
                StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                file => ReleaseOcrCohortCatalog.NormalizeRelativePath(file.Path)!,
                StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        using (var packageStream = new MemoryStream(package, writable: false))
        using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: false))
        {
            if (archive.Entries.Count > ReleaseOcrCohortCatalog.MaxFiles * 8)
                return null;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = ReleaseOcrCohortCatalog.NormalizeRelativePath(entry.FullName);
                if (string.IsNullOrEmpty(entry.Name))
                {
                    if (normalized is null || cohort.TrainedDataSource is null &&
                        !expected.Keys.Any(path => path.StartsWith(
                            normalized + "/",
                            StringComparison.OrdinalIgnoreCase)))
                        return null;
                    continue;
                }
                if (normalized is null) return null;
                if (!expected.TryGetValue(normalized, out var expectedFile))
                {
                    // The release package contains managed/x86/NuGet metadata,
                    // but only the exact x64 inventory is ever materialized.
                    if (cohort.TrainedDataSource is null) return null;
                    continue;
                }
                if (result.ContainsKey(normalized) ||
                    entry.Length != expectedFile.SizeBytes ||
                    entry.Length > ReleaseOcrCohortCatalog.MaxFileBytes)
                    return null;
                await using var entryStream = entry.Open();
                var bytes = await ReadExactAsync(
                        entryStream,
                        expectedFile.SizeBytes,
                        expectedFile.Sha256,
                        ReleaseOcrCohortCatalog.MaxFileBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (bytes is null) return null;
                result.Add(normalized, bytes);
            }
        }
        if (result.Count != expected.Count) return null;

        if (cohort.TrainedDataSource is { } trainedData)
        {
            var bytes = await DownloadExactAsync(
                    http,
                    trainedData.Url,
                    trainedData.SizeBytes,
                    trainedData.Sha256,
                    ReleaseOcrCohortCatalog.MaxFileBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var destination = ReleaseOcrCohortCatalog.NormalizeRelativePath(
                trainedData.DestinationPath);
            if (bytes is null || destination is null || result.ContainsKey(destination))
                return null;
            result.Add(destination, bytes);
        }
        return result.Count == cohort.Files.Count ? result : null;
    }

    private static async Task<byte[]?> DownloadExactAsync(
        HttpClient http,
        string url,
        long expectedBytes,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode ||
            response.Content.Headers.ContentLength is long declared &&
            declared != expectedBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadExactAsync(
                stream,
                expectedBytes,
                expectedSha256,
                maximumBytes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadExactAsync(
        Stream source,
        long expectedBytes,
        string expectedSha256,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (expectedBytes <= 0 || expectedBytes > maximumBytes ||
            expectedBytes > int.MaxValue)
            return null;
        using var output = new MemoryStream(checked((int)expectedBytes));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total = checked(total + read);
            if (total > expectedBytes) return null;
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return total == expectedBytes && FixedHashEquals(actual, expectedSha256)
            ? output.ToArray()
            : null;
    }

    private static string CohortsRoot(string dataDirectory) => Path.Combine(
        VisionRoot(dataDirectory),
        "cohorts");

    private static string VisionRoot(string dataDirectory) => Path.Combine(
        Path.GetFullPath(dataDirectory),
        "vision");

    private static void EnsureDirectoryChain(string dataRoot, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Vision cohort path escaped the data root.");
        Directory.CreateDirectory(target);
        EnsureExistingPathIsRegularDirectory(target);
    }

    private static void EnsureExistingPathIsRegularDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) ||
            attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Vision cohort path is not a regular directory.");
    }

    private static bool FixedHashEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));
}
