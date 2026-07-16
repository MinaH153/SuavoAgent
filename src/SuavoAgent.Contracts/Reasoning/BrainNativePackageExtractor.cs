using System.Collections.Frozen;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

namespace SuavoAgent.Contracts.Reasoning;

public sealed record BrainNativePackageResult(
    bool IsValid,
    string Code,
    IReadOnlyList<InstalledBrainFileManifest>? NativeFiles = null,
    bool IsOfficialNuGetLayout = false)
{
    internal static BrainNativePackageResult Reject(string code) => new(false, code);
}

/// <summary>
/// Inspects and extracts the exact universal Windows CPU backend used by the
/// local Brain. The preferred package is the immutable
/// LLamaSharp.Backend.Cpu 0.24.0 NuGet package. Only its five NOAVX DLLs are
/// flattened into the protected cohort; every other RID/AVX asset remains in
/// the retained package and is never executable.
///
/// A narrowly bounded legacy layout remains readable so already-installed,
/// publisher-authorized flat packages can be re-proved during upgrade. It may
/// contain only the four required text-inference DLLs plus optional
/// llava_shared.dll. New packages must use the NuGet layout.
/// </summary>
public static class BrainNativePackageExtractor
{
    public const string OfficialNuGetPackageKind =
        "nuget-llamasharp-backend-cpu-noavx-v1";
    public const string PackageId = "LLamaSharp.Backend.Cpu";
    public const string PackageVersion = "0.24.0";
    public const string NuGetPrefix = "runtimes/win-x64/native/noavx/";
    public const int MaxArchiveEntries = 256;
    public const long MaxArchiveUncompressedBytes = 128L * 1024 * 1024;
    public const long MaxEntryBytes = 64L * 1024 * 1024;

    private const string NuSpecName = "LLamaSharp.Backend.Cpu.nuspec";
    private const string SignatureName = ".signature.p7s";
    private const int MaxNuSpecBytes = 64 * 1024;
    private const int MaxSignatureBytes = 64 * 1024;

    private static readonly ImmutableArray<string> RequiredDlls =
    [
        "ggml-base.dll",
        "ggml-cpu.dll",
        "ggml.dll",
        "llama.dll",
        "llava_shared.dll",
    ];

    private static readonly FrozenSet<string> RequiredDllSet =
        RequiredDlls.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> LegacyRequiredDllSet =
        RequiredDlls
            .Where(name => name != "llava_shared.dll")
            .ToFrozenSet(StringComparer.Ordinal);

    public static Task<BrainNativePackageResult> InspectAsync(
        string packagePath,
        string nativePackageKind,
        CancellationToken ct) =>
        ProcessAsync(
            packagePath,
            nativeDirectory: null,
            nativePackageKind,
            allowLegacyFlatLayout: false,
            ct);

    /// <summary>
    /// Extracts into a new or empty non-reparse directory. Callers stage this
    /// directory and own its atomic activation/cleanup.
    /// </summary>
    public static Task<BrainNativePackageResult> ExtractAsync(
        string packagePath,
        string nativeDirectory,
        string nativePackageKind,
        CancellationToken ct) =>
        ProcessAsync(
            packagePath,
            nativeDirectory,
            nativePackageKind,
            allowLegacyFlatLayout: false,
            ct);

    /// <summary>
    /// Verification-only seam for cohorts installed under the retired schema-v2
    /// flat-ZIP contract. No provisioning caller can select this layout.
    /// </summary>
    internal static Task<BrainNativePackageResult> InspectLegacyFlatAsync(
        string packagePath,
        CancellationToken ct) =>
        ProcessAsync(
            packagePath,
            nativeDirectory: null,
            nativePackageKind: null,
            allowLegacyFlatLayout: true,
            ct);

    private static async Task<BrainNativePackageResult> ProcessAsync(
        string packagePath,
        string? nativeDirectory,
        string? nativePackageKind,
        bool allowLegacyFlatLayout,
        CancellationToken ct)
    {
        string? extractionRoot = null;
        try
        {
            if (!allowLegacyFlatLayout &&
                !string.Equals(
                    nativePackageKind,
                    OfficialNuGetPackageKind,
                    StringComparison.Ordinal))
                return BrainNativePackageResult.Reject("native_package_kind_invalid");
            if (!IsRegularFile(packagePath))
                return BrainNativePackageResult.Reject("native_package_file_invalid");

            extractionRoot = PrepareExtractionRoot(nativeDirectory);
            if (nativeDirectory is not null && extractionRoot is null)
                return BrainNativePackageResult.Reject("native_package_target_invalid");

            using var archive = ZipFile.OpenRead(packagePath);
            var scanned = ScanArchive(archive);
            if (!scanned.IsValid || scanned.Entries is null)
                return BrainNativePackageResult.Reject(scanned.Code);

            var selection = await SelectLayoutAsync(
                    scanned.Entries,
                    nativePackageKind,
                    allowLegacyFlatLayout,
                    ct)
                .ConfigureAwait(false);
            if (!selection.IsValid || selection.Entries is null)
                return BrainNativePackageResult.Reject(selection.Code);

            var installed = new List<InstalledBrainFileManifest>(selection.Entries.Count);
            foreach (var selected in selection.Entries.OrderBy(
                         entry => entry.FlatName,
                         StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var manifest = await ReadSelectedEntryAsync(
                        selected,
                        extractionRoot,
                        ct)
                    .ConfigureAwait(false);
                if (manifest is null)
                {
                    CleanupExtractedFiles(extractionRoot);
                    return BrainNativePackageResult.Reject("native_package_entry_read_failed");
                }
                installed.Add(manifest);
            }

            return new(
                true,
                "valid",
                installed,
                selection.IsOfficialNuGetLayout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CleanupExtractedFiles(extractionRoot);
            throw;
        }
        catch (Exception exception) when (exception is
                   IOException or UnauthorizedAccessException or InvalidDataException or
                   NotSupportedException or ArgumentException or XmlException or
                   CryptographicException)
        {
            CleanupExtractedFiles(extractionRoot);
            return BrainNativePackageResult.Reject("native_package_processing_failed");
        }
    }

    private static ArchiveScan ScanArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is <= 0 or > MaxArchiveEntries)
            return ArchiveScan.Reject("native_package_entry_count_invalid");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<ScannedEntry>(archive.Entries.Count);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryPath(entry.FullName);
            if (normalized is null)
                return ArchiveScan.Reject("native_package_entry_path_invalid");
            if (!seen.Add(normalized))
                return ArchiveScan.Reject("native_package_duplicate_entry");
            if (IsLinkOrReparse(entry))
                return ArchiveScan.Reject("native_package_reparse_entry");

            var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                              entry.FullName.EndsWith("\\", StringComparison.Ordinal);
            if (isDirectory && entry.Length != 0)
                return ArchiveScan.Reject("native_package_directory_invalid");
            if (!isDirectory &&
                (entry.Length < 0 || entry.Length > MaxEntryBytes ||
                 total > MaxArchiveUncompressedBytes - entry.Length))
                return ArchiveScan.Reject("native_package_uncompressed_bounds_invalid");
            if (!isDirectory) total += entry.Length;
            entries.Add(new(entry, normalized, isDirectory));
        }
        return new(true, "valid", entries);
    }

    private static async Task<LayoutSelection> SelectLayoutAsync(
        IReadOnlyList<ScannedEntry> entries,
        string? nativePackageKind,
        bool allowLegacyFlatLayout,
        CancellationToken ct)
    {
        var files = entries.Where(entry => !entry.IsDirectory).ToArray();
        var nuSpec = files.Where(entry => entry.Path == NuSpecName).ToArray();
        var signature = files.Where(entry => entry.Path == SignatureName).ToArray();
        var selectedNuGet = files
            .Where(entry => entry.Path.StartsWith(NuGetPrefix, StringComparison.Ordinal))
            .ToArray();
        var resemblesNuGet = nuSpec.Length > 0 || signature.Length > 0 || selectedNuGet.Length > 0;

        if (resemblesNuGet)
        {
            if (!string.Equals(
                    nativePackageKind,
                    OfficialNuGetPackageKind,
                    StringComparison.Ordinal))
                return LayoutSelection.Reject("native_package_kind_invalid");
            if (nuSpec.Length != 1 || signature.Length != 1 ||
                signature[0].Entry.Length is <= 0 or > MaxSignatureBytes ||
                !await HasExactNuGetIdentityAsync(nuSpec[0].Entry, ct).ConfigureAwait(false))
                return LayoutSelection.Reject("native_package_nuget_identity_invalid");

            var selected = new List<SelectedEntry>(RequiredDlls.Length);
            foreach (var entry in selectedNuGet)
            {
                var relative = entry.Path[NuGetPrefix.Length..];
                if (relative.Contains('/') || !RequiredDllSet.Contains(relative))
                    return LayoutSelection.Reject("native_package_nuget_selected_entry_invalid");
                selected.Add(new(entry.Entry, relative));
            }
            if (selected.Count != RequiredDlls.Length ||
                !selected.Select(item => item.FlatName).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(RequiredDllSet))
                return LayoutSelection.Reject("native_package_nuget_selected_entry_missing");
            return new(true, "valid", selected, IsOfficialNuGetLayout: true);
        }

        // Compatibility is deliberately exact: no folders, metadata, or arbitrary
        // native files survive the transition from the former signed flat ZIP.
        if (!allowLegacyFlatLayout)
            return LayoutSelection.Reject("native_package_official_layout_required");
        if (entries.Any(entry => entry.IsDirectory) ||
            files.Any(entry => entry.Path.Contains('/')))
            return LayoutSelection.Reject("native_package_legacy_layout_invalid");
        var legacyNames = files.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
        if (!LegacyRequiredDllSet.IsSubsetOf(legacyNames) ||
            legacyNames.Any(name => !RequiredDllSet.Contains(name)) ||
            legacyNames.Count is < 4 or > 5)
            return LayoutSelection.Reject("native_package_legacy_layout_invalid");
        return new(
            true,
            "valid",
            files.Select(entry => new SelectedEntry(entry.Entry, entry.Path)).ToArray(),
            IsOfficialNuGetLayout: false);
    }

    private static async Task<bool> HasExactNuGetIdentityAsync(
        ZipArchiveEntry entry,
        CancellationToken ct)
    {
        if (entry.Length is <= 0 or > MaxNuSpecBytes) return false;
        await using var stream = entry.Open();
        using var bounded = new MemoryStream((int)entry.Length);
        var buffer = new byte[16 * 1024];
        long written = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (written > entry.Length - read || written > MaxNuSpecBytes - read)
                return false;
            await bounded.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            written += read;
        }
        if (written != entry.Length) return false;
        bounded.Position = 0;
        using var reader = XmlReader.Create(bounded, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxNuSpecBytes,
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var metadataNodes = document.Descendants()
            .Where(node => node.Name.LocalName == "metadata")
            .Take(2)
            .ToArray();
        if (metadataNodes.Length != 1) return false;
        var metadata = metadataNodes[0];
        string? Value(string name)
        {
            var matches = metadata.Elements()
                .Where(node => node.Name.LocalName == name)
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0].Value.Trim() : null;
        }
        var licenses = metadata.Elements()
            .Where(node => node.Name.LocalName == "license")
            .Take(2)
            .ToArray();
        if (licenses.Length != 1) return false;
        var license = licenses[0];
        return Value("id") == PackageId &&
               Value("version") == PackageVersion &&
               license?.Attribute("type")?.Value == "expression" &&
               license.Value.Trim() == "MIT";
    }

    private static async Task<InstalledBrainFileManifest?> ReadSelectedEntryAsync(
        SelectedEntry selected,
        string? extractionRoot,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = selected.Entry.Open();
        FileStream? output = null;
        try
        {
            if (extractionRoot is not null)
            {
                var path = SafeFlatPath(extractionRoot, selected.FlatName);
                output = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.WriteThrough);
            }

            var buffer = new byte[128 * 1024];
            long written = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                if (written > selected.Entry.Length - read ||
                    written > MaxEntryBytes - read)
                    return null;
                if (output is not null)
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                written += read;
            }
            if (written != selected.Entry.Length) return null;
            if (output is not null)
            {
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            return new(
                selected.FlatName,
                written,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally
        {
            if (output is not null) await output.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string? PrepareExtractionRoot(string? nativeDirectory)
    {
        if (nativeDirectory is null) return null;
        var root = Path.GetFullPath(nativeDirectory);
        if (Directory.Exists(root))
        {
            if (IsReparse(root) || Directory.EnumerateFileSystemEntries(root).Any()) return null;
        }
        else
        {
            Directory.CreateDirectory(root);
            if (IsReparse(root)) return null;
        }
        return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
               Path.DirectorySeparatorChar;
    }

    private static void CleanupExtractedFiles(string? extractionRoot)
    {
        if (extractionRoot is null) return;
        try
        {
            foreach (var name in RequiredDlls)
            {
                var path = SafeFlatPath(extractionRoot, name);
                if (File.Exists(path) && !IsReparse(path)) File.Delete(path);
            }
        }
        catch
        {
            // Callers own their private staging directory and delete it after a
            // rejection. This best-effort pass only narrows the partial-file
            // window before that outer cleanup executes.
        }
    }

    private static string SafeFlatPath(string root, string name)
    {
        if (!RequiredDllSet.Contains(name) || name != Path.GetFileName(name))
            throw new InvalidDataException("Selected native file name is not allowed.");
        var path = Path.GetFullPath(Path.Combine(root, name));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(root, comparison))
            throw new InvalidDataException("Selected native file escaped its cohort.");
        return path;
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

    private static bool IsRegularFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0 && !IsReparse(path);
    }

    private static bool IsLinkOrReparse(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        return unixType == 0xA000 ||
               (windowsAttributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private sealed record ScannedEntry(
        ZipArchiveEntry Entry,
        string Path,
        bool IsDirectory);

    private sealed record SelectedEntry(ZipArchiveEntry Entry, string FlatName);

    private sealed record ArchiveScan(
        bool IsValid,
        string Code,
        IReadOnlyList<ScannedEntry>? Entries = null)
    {
        internal static ArchiveScan Reject(string code) => new(false, code);
    }

    private sealed record LayoutSelection(
        bool IsValid,
        string Code,
        IReadOnlyList<SelectedEntry>? Entries = null,
        bool IsOfficialNuGetLayout = false)
    {
        internal static LayoutSelection Reject(string code) => new(false, code);
    }
}
