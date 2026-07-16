using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Vision;

internal sealed record TesseractNativeFile(
    string Path,
    long SizeBytes,
    string Sha256);

/// <summary>
/// Exact, immutable language-data source that completes a repository-signed
/// native wrapper package. It is part of the compiled cohort identity and is
/// never supplied by the control plane.
/// </summary>
internal sealed record TesseractTrainedDataSource(
    string Url,
    string Sha256,
    long SizeBytes,
    string DestinationPath);

internal sealed record TesseractNativeCohort(
    int SchemaVersion,
    string CohortId,
    string BundleUrl,
    string BundleSha256,
    long BundleSizeBytes,
    IReadOnlyList<TesseractNativeFile> Files)
{
    internal TesseractTrainedDataSource? TrainedDataSource { get; init; }

    internal static TesseractNativeCohort Create(
        string bundleUrl,
        string bundleSha256,
        long bundleSizeBytes,
        IReadOnlyList<TesseractNativeFile> files,
        TesseractTrainedDataSource? trainedDataSource = null)
    {
        var unsigned = new TesseractNativeCohort(
            TesseractNativeCohortPolicy.SchemaVersion,
            string.Empty,
            bundleUrl,
            bundleSha256,
            bundleSizeBytes,
            files)
        {
            TrainedDataSource = trainedDataSource,
        };
        return unsigned with
        {
            CohortId = TesseractNativeCohortPolicy.ComputeCohortId(unsigned),
        };
    }
}

/// <summary>
/// Offline release allow-list for native OCR cohorts. Every future entry must
/// carry the exact archive identity and a complete per-file inventory. The
/// policy is compiled into the Authenticode-signed Core and Helper binaries;
/// a cloud command can select an entry but cannot mint one.
/// </summary>
public static class TesseractNativeCohortPolicy
{
    internal const int SchemaVersion = ReleaseOcrCohortCatalog.SchemaVersion;
    internal const string ManifestFileName = ReleaseOcrCohortCatalog.ManifestFileName;
    internal const long MaxBundleBytes = ReleaseOcrCohortCatalog.MaxBundleBytes;
    internal const long MaxFileBytes = ReleaseOcrCohortCatalog.MaxFileBytes;
    internal const long MaxExtractedBytes = ReleaseOcrCohortCatalog.MaxExtractedBytes;
    internal const int MaxFiles = ReleaseOcrCohortCatalog.MaxFiles;
    internal const int MaxManifestBytes = ReleaseOcrCohortCatalog.MaxManifestBytes;

    private static readonly IReadOnlyDictionary<string, TesseractNativeCohort> ApprovedById =
        new ReadOnlyDictionary<string, TesseractNativeCohort>(
            ReleaseOcrCohortCatalog.Approved
                .Select(FromReleaseDescriptor)
                .ToDictionary(cohort => cohort.CohortId, StringComparer.Ordinal));

    public static bool HasReleaseApprovedCohorts => ApprovedById.Count > 0;

    internal static TesseractNativeCohort? Resolve(string bundleUrl, string sha256)
    {
        var normalizedHash = sha256.Trim().ToLowerInvariant();
        return ApprovedById.Values.SingleOrDefault(cohort =>
            string.Equals(cohort.BundleUrl, bundleUrl, StringComparison.Ordinal) &&
            FixedHashEquals(cohort.BundleSha256, normalizedHash) &&
            IsWellFormed(cohort));
    }

    internal static bool IsReleaseApproved(string bundleUrl, string sha256) =>
        Resolve(bundleUrl, sha256) is not null;

    /// <summary>
    /// Helper-side runtime gate. It resolves the exact compiled cohort and
    /// hashes every executable/data file immediately before native load.
    /// </summary>
    public static bool VerifyInstalled(TesseractOptions? options)
    {
        if (!TryResolveConfigured(options, out var cohort))
            return false;

        var cohortsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "vision",
            "cohorts");
        return VerifyInstalledAt(options!.NativeLibraryPath!, cohortsRoot, cohort);
    }

    /// <summary>
    /// Post-load binding for the wrapper's process-global native resolver. The
    /// caller supplies only modules whose names match the reviewed OCR DLLs;
    /// every one must originate from the exact content-addressed x64 folder.
    /// </summary>
    public static bool VerifyLoadedNativeModulePaths(
        TesseractOptions? options,
        IReadOnlyDictionary<string, string>? loadedModules)
    {
        if (!TryResolveConfigured(options, out var cohort) ||
            loadedModules is null)
            return false;
        var cohortsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "vision",
            "cohorts");
        return VerifyLoadedNativeModulePathsAt(
            options!,
            loadedModules,
            cohort,
            cohortsRoot);
    }

    internal static bool VerifyLoadedNativeModulePathsAt(
        TesseractOptions options,
        IReadOnlyDictionary<string, string> loadedModules,
        TesseractNativeCohort cohort,
        string cohortsRoot)
    {
        if (!ConfigurationMatches(options, cohort) ||
            !VerifyInstalledAt(options.NativeLibraryPath!, cohortsRoot, cohort))
            return false;
        var expected = cohort.Files
            .Where(file =>
                file.Path.StartsWith("x64/", StringComparison.OrdinalIgnoreCase) &&
                file.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                file => Path.GetFileName(file.Path),
                file => Path.Combine(
                    options!.NativeLibraryPath!,
                    file.Path.Replace('/', Path.DirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0 || loadedModules.Count != expected.Count)
            return false;
        return expected.All(item =>
            loadedModules.TryGetValue(item.Key, out var actualPath) &&
            PathEquals(actualPath, item.Value));
    }

    private static bool TryResolveConfigured(
        TesseractOptions? options,
        out TesseractNativeCohort cohort)
    {
        cohort = null!;
        if (options is null ||
            string.IsNullOrWhiteSpace(options.CohortId) ||
            !ApprovedById.TryGetValue(options.CohortId, out var configured) ||
            configured is null ||
            !ConfigurationMatches(options, configured))
            return false;
        cohort = configured;
        return true;
    }

    private static bool ConfigurationMatches(
        TesseractOptions options,
        TesseractNativeCohort cohort)
    {
        if (string.IsNullOrWhiteSpace(options.BundleSha256) ||
            string.IsNullOrWhiteSpace(options.ManifestSha256) ||
            !string.Equals(options.CohortId, cohort.CohortId, StringComparison.Ordinal) ||
            !FixedHashEquals(options.BundleSha256, cohort.BundleSha256) ||
            !FixedHashEquals(options.ManifestSha256, ComputeManifestSha256(cohort)) ||
            string.IsNullOrWhiteSpace(options.NativeLibraryPath) ||
            !string.Equals(options.Language, "eng", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(options.TessdataPath) ||
            !PathEquals(
                options.TessdataPath,
                Path.Combine(options.NativeLibraryPath, "tessdata")))
            return false;
        return true;
    }

    internal static bool VerifyInstalledAt(
        string cohortRoot,
        string cohortsRoot,
        TesseractNativeCohort cohort) =>
        ReleaseOcrCohortCatalog.VerifyInstalledAt(
            cohortRoot,
            cohortsRoot,
            ToReleaseDescriptor(cohort));

    internal static byte[] SerializeManifest(TesseractNativeCohort cohort)
    {
        if (!IsWellFormed(cohort))
            throw new InvalidDataException("Tesseract cohort manifest is invalid.");
        return ReleaseOcrCohortCatalog.SerializeManifest(ToReleaseDescriptor(cohort));
    }

    internal static string ComputeManifestSha256(TesseractNativeCohort cohort) =>
        ReleaseOcrCohortCatalog.ComputeManifestSha256(ToReleaseDescriptor(cohort));

    internal static string ComputeCohortId(TesseractNativeCohort cohort) =>
        ReleaseOcrCohortCatalog.ComputeCohortId(ToReleaseDescriptor(cohort));

    internal static bool IsWellFormed(TesseractNativeCohort cohort) =>
        ReleaseOcrCohortCatalog.IsWellFormed(ToReleaseDescriptor(cohort));

    internal static string? NormalizeRelativePath(string value) =>
        ReleaseOcrCohortCatalog.NormalizeRelativePath(value);

    internal static string SafeEntryPath(string root, string relative) =>
        ReleaseOcrCohortCatalog.SafeEntryPath(root, relative);

    private static TesseractNativeCohort FromReleaseDescriptor(
        ReleaseOcrCohort cohort) => new(
        cohort.SchemaVersion,
        cohort.CohortId,
        cohort.BundleUrl,
        cohort.BundleSha256,
        cohort.BundleSizeBytes,
        cohort.Files.Select(file => new TesseractNativeFile(
            file.Path,
            file.SizeBytes,
            file.Sha256)).ToArray())
    {
        TrainedDataSource = cohort.TrainedDataSource is null
            ? null
            : new TesseractTrainedDataSource(
                cohort.TrainedDataSource.Url,
                cohort.TrainedDataSource.Sha256,
                cohort.TrainedDataSource.SizeBytes,
                cohort.TrainedDataSource.DestinationPath),
    };

    private static ReleaseOcrCohort ToReleaseDescriptor(
        TesseractNativeCohort cohort) => new(
        cohort.SchemaVersion,
        cohort.CohortId,
        cohort.BundleUrl,
        cohort.BundleSha256,
        cohort.BundleSizeBytes,
        cohort.Files.Select(file => new ReleaseOcrFile(
            file.Path,
            file.SizeBytes,
            file.Sha256)).ToArray())
    {
        TrainedDataSource = cohort.TrainedDataSource is null
            ? null
            : new ReleaseOcrTrainedDataSource(
                cohort.TrainedDataSource.Url,
                cohort.TrainedDataSource.Sha256,
                cohort.TrainedDataSource.SizeBytes,
                cohort.TrainedDataSource.DestinationPath),
    };

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool FixedHashEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }
}
