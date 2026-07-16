using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Vision;

public sealed record ReleaseOcrFile(
    string Path,
    long SizeBytes,
    string Sha256);

public sealed record ReleaseOcrTrainedDataSource(
    string Url,
    string Sha256,
    long SizeBytes,
    string DestinationPath);

/// <summary>
/// Immutable native OCR identity compiled into every component that may stage,
/// select, or load it. The control plane can select an entry from this catalog;
/// it cannot supply executable inventory or download locations.
/// </summary>
public sealed record ReleaseOcrCohort(
    int SchemaVersion,
    string CohortId,
    string BundleUrl,
    string BundleSha256,
    long BundleSizeBytes,
    IReadOnlyList<ReleaseOcrFile> Files)
{
    public ReleaseOcrTrainedDataSource? TrainedDataSource { get; init; }

    public static ReleaseOcrCohort Create(
        string bundleUrl,
        string bundleSha256,
        long bundleSizeBytes,
        IReadOnlyList<ReleaseOcrFile> files,
        ReleaseOcrTrainedDataSource? trainedDataSource = null)
    {
        var unsigned = new ReleaseOcrCohort(
            ReleaseOcrCohortCatalog.SchemaVersion,
            string.Empty,
            bundleUrl,
            bundleSha256,
            bundleSizeBytes,
            Array.AsReadOnly(files.ToArray()))
        {
            TrainedDataSource = trainedDataSource,
        };
        return unsigned with
        {
            CohortId = ReleaseOcrCohortCatalog.ComputeCohortId(unsigned),
        };
    }
}

internal sealed record InstalledReleaseOcrManifest(
    int SchemaVersion,
    string CohortId,
    string BundleUrl,
    string BundleSha256,
    long BundleSizeBytes,
    ReleaseOcrTrainedDataSource? TrainedDataSource,
    IReadOnlyList<ReleaseOcrFile> Files);

/// <summary>
/// Single repository-signed release catalog and deterministic manifest codec.
/// Setup provisions these bytes; Core only selects and verifies them; Helper
/// verifies the same identity again immediately before native load.
/// </summary>
public static class ReleaseOcrCohortCatalog
{
    public const int SchemaVersion = 2;
    public const string ManifestFileName = "tesseract.manifest.json";
    public const long MaxBundleBytes = 256L * 1024 * 1024;
    public const long MaxFileBytes = 128L * 1024 * 1024;
    public const long MaxExtractedBytes = 512L * 1024 * 1024;
    public const int MaxFiles = 32;
    public const int MaxManifestBytes = 128 * 1024;
    private const string IdentityDomain = "suavo-tesseract-native-cohort-v2";

    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        WriteIndented = false,
    };

    private static readonly ReleaseOcrCohort Tesseract520English =
        ReleaseOcrCohort.Create(
            "https://api.nuget.org/v3-flatcontainer/tesseract/5.2.0/" +
            "tesseract.5.2.0.nupkg",
            "202d82fc7c7d8384df7da57206d5e1f456ccdabd648c46e67cdfaa3a911d4795",
            5_697_774,
            new ReleaseOcrFile[]
            {
                new(
                    "x64/leptonica-1.82.0.dll",
                    4_168_192,
                    "dfcb3e6ed0b16bc55bfdbcf53543cfe42a354b87c3e35bd3a95eebf005d73e76"),
                new(
                    "x64/tesseract50.dll",
                    2_788_352,
                    "de4d04ec75095374d98f5dd7a60d14d7e2e0f76589db693eccf7ae658be8cb2b"),
                new(
                    "tessdata/eng.traineddata",
                    4_113_088,
                    "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2"),
            },
            new ReleaseOcrTrainedDataSource(
                "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/" +
                "65727574dfcd264acbb0c3e07860e4e9e9b22185/eng.traineddata",
                "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2",
                4_113_088,
                "tessdata/eng.traineddata"));

    private static readonly IReadOnlyList<ReleaseOcrCohort> ReleaseApproved =
        new ReadOnlyCollection<ReleaseOcrCohort>([Tesseract520English]);

    public static IReadOnlyList<ReleaseOcrCohort> Approved => ReleaseApproved;

    public static ReleaseOcrCohort? Resolve(string? bundleUrl, string? bundleSha256)
    {
        if (string.IsNullOrWhiteSpace(bundleUrl) ||
            string.IsNullOrWhiteSpace(bundleSha256))
            return null;
        var normalizedHash = bundleSha256.Trim().ToLowerInvariant();
        return ReleaseApproved.SingleOrDefault(cohort =>
            string.Equals(cohort.BundleUrl, bundleUrl, StringComparison.Ordinal) &&
            FixedHashEquals(cohort.BundleSha256, normalizedHash) &&
            IsWellFormed(cohort));
    }

    public static bool IsWellFormed(ReleaseOcrCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        if (cohort.SchemaVersion != SchemaVersion ||
            !IsSafeHttpsUrl(cohort.BundleUrl) ||
            !IsLowerSha256(cohort.BundleSha256) ||
            cohort.BundleSizeBytes is <= 0 or > MaxBundleBytes ||
            cohort.Files.Count is <= 0 or > MaxFiles ||
            !FixedHashEquals(cohort.CohortId, ComputeCohortId(cohort)))
            return false;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long extractedBytes = 0;
        foreach (var file in cohort.Files)
        {
            var normalized = NormalizeRelativePath(file.Path);
            if (normalized is null || !paths.Add(normalized) ||
                file.SizeBytes is <= 0 or > MaxFileBytes ||
                !IsLowerSha256(file.Sha256))
                return false;
            extractedBytes = checked(extractedBytes + file.SizeBytes);
            if (extractedBytes > MaxExtractedBytes) return false;
        }

        if (cohort.TrainedDataSource is { } trainedData)
        {
            var destination = NormalizeRelativePath(trainedData.DestinationPath);
            if (!IsSafeHttpsUrl(trainedData.Url) ||
                !IsLowerSha256(trainedData.Sha256) ||
                trainedData.SizeBytes is <= 0 or > MaxFileBytes ||
                destination is null || !paths.Contains(destination))
                return false;
            var matching = cohort.Files.SingleOrDefault(file => string.Equals(
                NormalizeRelativePath(file.Path),
                destination,
                StringComparison.OrdinalIgnoreCase));
            if (matching is null || matching.SizeBytes != trainedData.SizeBytes ||
                !FixedHashEquals(matching.Sha256, trainedData.Sha256))
                return false;
        }

        return paths.Contains("x64/tesseract50.dll") &&
               paths.Contains("tessdata/eng.traineddata") &&
               paths.Any(path =>
                   path.StartsWith("x64/leptonica-", StringComparison.OrdinalIgnoreCase) &&
                   path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
    }

    public static byte[] SerializeManifest(ReleaseOcrCohort cohort)
    {
        if (!IsWellFormed(cohort))
            throw new InvalidDataException("Release OCR cohort is invalid.");
        return JsonSerializer.SerializeToUtf8Bytes(
            new InstalledReleaseOcrManifest(
                cohort.SchemaVersion,
                cohort.CohortId,
                cohort.BundleUrl,
                cohort.BundleSha256,
                cohort.BundleSizeBytes,
                cohort.TrainedDataSource,
                cohort.Files),
            ManifestJson);
    }

    public static string ComputeManifestSha256(ReleaseOcrCohort cohort) =>
        Convert.ToHexString(SHA256.HashData(SerializeManifest(cohort)))
            .ToLowerInvariant();

    public static string ComputeCohortId(ReleaseOcrCohort cohort)
    {
        ArgumentNullException.ThrowIfNull(cohort);
        var canonical = string.Join('|',
            IdentityDomain,
            cohort.SchemaVersion,
            cohort.BundleUrl,
            cohort.BundleSha256,
            cohort.BundleSizeBytes,
            cohort.TrainedDataSource is null
                ? string.Empty
                : string.Join(',',
                    cohort.TrainedDataSource.Url,
                    cohort.TrainedDataSource.Sha256,
                    cohort.TrainedDataSource.SizeBytes,
                    cohort.TrainedDataSource.DestinationPath),
            string.Join(';', cohort.Files.Select(file =>
                $"{file.Path},{file.SizeBytes},{file.Sha256}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static bool VerifyInstalledAt(
        string cohortRoot,
        string cohortsRoot,
        ReleaseOcrCohort cohort)
    {
        try
        {
            if (!IsWellFormed(cohort) ||
                !PathEquals(
                    cohortRoot,
                    Path.Combine(Path.GetFullPath(cohortsRoot), cohort.BundleSha256)) ||
                !Directory.Exists(cohortRoot) || IsReparse(cohortRoot))
                return false;

            var actualFiles = EnumerateTreeFilesWithoutReparse(cohortRoot);
            if (actualFiles is null) return false;
            var manifestPath = Path.Combine(cohortRoot, ManifestFileName);
            var manifestInfo = new FileInfo(manifestPath);
            if (!manifestInfo.Exists || manifestInfo.Length is <= 0 or > MaxManifestBytes ||
                IsReparse(manifestPath))
                return false;
            var manifestBytes = File.ReadAllBytes(manifestPath);
            if (!FixedHashEquals(
                    Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(),
                    ComputeManifestSha256(cohort)))
                return false;
            var persisted = JsonSerializer.Deserialize<InstalledReleaseOcrManifest>(
                manifestBytes,
                ManifestJson);
            if (!ManifestEquals(persisted, cohort)) return false;

            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ManifestFileName,
            };
            foreach (var file in cohort.Files)
            {
                var normalized = NormalizeRelativePath(file.Path);
                if (normalized is null || !expected.Add(normalized)) return false;
                var path = SafeEntryPath(cohortRoot, normalized);
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != file.SizeBytes || IsReparse(path))
                    return false;
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    128 * 1024,
                    FileOptions.SequentialScan);
                var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (!FixedHashEquals(actual, file.Sha256)) return false;
            }
            return actualFiles.Count == expected.Count && actualFiles.All(expected.Contains);
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or ArgumentException or
                   NotSupportedException or JsonException or CryptographicException or
                   OverflowException)
        {
            return false;
        }
    }

    public static string? NormalizeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(char.IsControl))
            return null;
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || normalized.StartsWith('/') ||
            normalized.Contains("//", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment => segment is "" or "." or "..") ||
            Path.IsPathRooted(normalized) || normalized.Contains(':', StringComparison.Ordinal))
            return null;
        return normalized;
    }

    public static string SafeEntryPath(string root, string relative)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(
            canonicalRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Release OCR entry escaped its cohort root.");
        return candidate;
    }

    private static bool ManifestEquals(
        InstalledReleaseOcrManifest? persisted,
        ReleaseOcrCohort cohort) =>
        persisted is not null &&
        persisted.SchemaVersion == cohort.SchemaVersion &&
        persisted.CohortId == cohort.CohortId &&
        persisted.BundleUrl == cohort.BundleUrl &&
        FixedHashEquals(persisted.BundleSha256, cohort.BundleSha256) &&
        persisted.BundleSizeBytes == cohort.BundleSizeBytes &&
        persisted.TrainedDataSource == cohort.TrainedDataSource &&
        persisted.Files.SequenceEqual(cohort.Files);

    private static IReadOnlyList<string>? EnumerateTreeFilesWithoutReparse(string root)
    {
        if (!Directory.Exists(root) || IsReparse(root)) return null;
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        var visited = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (++visited > MaxFiles * 4 || IsReparse(entry)) return null;
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }
                if (!File.Exists(entry)) return null;
                files.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        }
        return files;
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsSafeHttpsUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool FixedHashEquals(string? left, string? right)
    {
        if (!IsLowerSha256(left) || !IsLowerSha256(right)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left!),
            Encoding.ASCII.GetBytes(right!));
    }
}
