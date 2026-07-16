using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper.Workflows;

internal sealed record XlsxExportStamp(long Length, long LastWriteUtcTicks);

internal sealed record XlsxExportBaseline(
    string RootDirectory,
    IReadOnlyDictionary<string, XlsxExportStamp> Files);

internal sealed record StableXlsxExport(
    string FullPath,
    string Sha256,
    long Length);

internal sealed record XlsxExportWatchResult(
    bool Success,
    bool InvalidStableFileObserved,
    StableXlsxExport? Export);

/// <summary>
/// Watches only the interactive user's top-level Downloads directory for a new
/// or changed, stable, structurally valid XLSX package. It never searches other
/// folders and never accepts nested paths, reparse-point files, temporary files,
/// legacy XLS, or a ZIP renamed to XLSX without the required workbook parts.
/// </summary>
internal sealed class StableXlsxExportWatcher
{
    private const int MaximumArchiveEntries = 2_048;
    private const long MaximumUncompressedBytes = 64 * 1024 * 1024;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _rootDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _stabilityInterval;

    public StableXlsxExportWatcher(
        string rootDirectory,
        TimeProvider? timeProvider = null,
        TimeSpan? pollInterval = null,
        TimeSpan? stabilityInterval = null)
    {
        _rootDirectory = Path.GetFullPath(rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory)));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _stabilityInterval = stabilityInterval ?? TimeSpan.FromMilliseconds(750);
    }

    public static string ResolveDefaultDownloadsDirectory()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? string.Empty
            : Path.Combine(profile, "Downloads");
    }

    public bool TryCaptureBaseline(out XlsxExportBaseline? baseline)
    {
        baseline = null;
        if (!Directory.Exists(_rootDirectory) || IsReparsePoint(_rootDirectory))
            return false;

        try
        {
            var files = EnumerateSafeFiles()
                .Select(path => (Path: path, Stamp: TryStamp(path)))
                .Where(entry => entry.Stamp is not null)
                .ToDictionary(
                    entry => entry.Path,
                    entry => entry.Stamp!,
                    PathComparer);
            baseline = new XlsxExportBaseline(_rootDirectory, files);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<XlsxExportWatchResult> WaitAsync(
        XlsxExportBaseline baseline,
        DateTimeOffset notBeforeUtc,
        TimeSpan timeout,
        CancellationToken ct,
        Func<StableXlsxExport, bool>? semanticValidator = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        if (!PathComparer.Equals(
                Path.GetFullPath(baseline.RootDirectory),
                _rootDirectory))
            return new(false, true, null);

        var deadline = _timeProvider.GetUtcNow() + timeout;
        var invalidStableFileObserved = false;
        var rejected = new Dictionary<string, XlsxExportStamp>(PathComparer);
        while (_timeProvider.GetUtcNow() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var candidate in ChangedCandidates(baseline, notBeforeUtc))
            {
                var first = TryStamp(candidate);
                if (first is null) continue;
                if (rejected.TryGetValue(candidate, out var rejectedStamp) &&
                    rejectedStamp == first)
                    continue;

                await Task.Delay(_stabilityInterval, _timeProvider, ct).ConfigureAwait(false);
                var second = TryStamp(candidate);
                if (second is null || second != first) continue;

                await Task.Delay(_stabilityInterval, _timeProvider, ct).ConfigureAwait(false);
                var third = TryStamp(candidate);
                if (third is null || third != second) continue;

                var export = TryValidateAndHash(candidate, third);
                if (export is not null)
                {
                    var semanticMatch = false;
                    try
                    {
                        semanticMatch = semanticValidator is null || semanticValidator(export);
                    }
                    catch
                    {
                        semanticMatch = false;
                    }
                    if (semanticMatch)
                        return new(true, invalidStableFileObserved, export);
                    rejected[candidate] = third;
                }

                invalidStableFileObserved = true;
            }

            await Task.Delay(_pollInterval, _timeProvider, ct).ConfigureAwait(false);
        }

        return new(false, invalidStableFileObserved, null);
    }

    private IEnumerable<string> ChangedCandidates(
        XlsxExportBaseline baseline,
        DateTimeOffset notBeforeUtc)
    {
        var earliestWrite = notBeforeUtc.UtcDateTime - TimeSpan.FromSeconds(2);
        return EnumerateSafeFiles()
            .Select(path => (Path: path, Stamp: TryStamp(path)))
            .Where(entry => entry.Stamp is not null)
            .Where(entry => new DateTime(entry.Stamp!.LastWriteUtcTicks, DateTimeKind.Utc) >= earliestWrite)
            .Where(entry => !baseline.Files.TryGetValue(entry.Path, out var before) || before != entry.Stamp)
            .OrderByDescending(entry => entry.Stamp!.LastWriteUtcTicks)
            .Select(entry => entry.Path)
            .ToArray();
    }

    private IEnumerable<string> EnumerateSafeFiles()
    {
        if (!Directory.Exists(_rootDirectory) || IsReparsePoint(_rootDirectory))
            return Array.Empty<string>();

        try
        {
            return Directory.EnumerateFiles(
                    _rootDirectory,
                    "*.xlsx",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .Where(IsSafeFilePath)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private bool IsSafeFilePath(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!PathComparer.Equals(Path.GetDirectoryName(path), _rootDirectory))
            return false;
        if (Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal))
            return false;

        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static XlsxExportStamp? TryStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && IsCandidateLengthAllowed(info.Length)
                ? new XlsxExportStamp(info.Length, info.LastWriteTimeUtc.Ticks)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static StableXlsxExport? TryValidateAndHash(
        string path,
        XlsxExportStamp expected)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length != expected.Length) return null;
            if (!TryValidateBoundedCentralDirectory(
                    stream,
                    out var declaredEntryCount))
                return null;

            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (archive.GetEntry("[Content_Types].xml") is null ||
                    archive.GetEntry("xl/workbook.xml") is null ||
                    archive.Entries.Count != declaredEntryCount ||
                    !ArchiveShapeIsBounded(
                        archive.Entries.Count,
                        archive.Entries.Select(entry => entry.Length)))
                    return null;
            }

            stream.Position = 0;
            var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            var after = TryStamp(path);
            return after == expected
                ? new StableXlsxExport(path, sha256, expected.Length)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryValidateBoundedCentralDirectory(
        Stream stream,
        out int entryCount)
    {
        entryCount = 0;
        const uint endOfCentralDirectorySignature = 0x06054b50;
        const int fixedRecordBytes = 22;
        const int maximumCommentBytes = ushort.MaxValue;
        if (!stream.CanSeek || stream.Length < fixedRecordBytes) return false;

        var tailLength = checked((int)Math.Min(
            stream.Length,
            fixedRecordBytes + maximumCommentBytes));
        var tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);
        for (var offset = tail.Length - fixedRecordBytes; offset >= 0; offset--)
        {
            var span = tail.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt32LittleEndian(span) !=
                endOfCentralDirectorySignature)
                continue;
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(span[20..]);
            if (offset + fixedRecordBytes + commentLength != tail.Length) continue;
            var diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(span[4..]);
            var centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
            var entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
            var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(span[10..]);
            var centralDirectoryBytes = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
            var recordOffset = stream.Length - tailLength + offset;
            if (diskNumber != 0 ||
                centralDirectoryDisk != 0 ||
                entriesOnDisk != totalEntries ||
                totalEntries is 0 or > MaximumArchiveEntries ||
                centralDirectoryBytes == 0 ||
                centralDirectoryOffset + (long)centralDirectoryBytes != recordOffset ||
                !TryCountBoundedCentralDirectoryRecords(
                    stream,
                    centralDirectoryOffset,
                    centralDirectoryBytes,
                    out var parsedEntries) ||
                parsedEntries != totalEntries)
                return false;
            entryCount = parsedEntries;
            stream.Position = 0;
            return true;
        }

        stream.Position = 0;
        return false;
    }

    private static bool TryCountBoundedCentralDirectoryRecords(
        Stream stream,
        long directoryOffset,
        long directoryBytes,
        out int entryCount)
    {
        entryCount = 0;
        const uint centralDirectoryHeaderSignature = 0x02014b50;
        const int fixedHeaderBytes = 46;
        var directoryEnd = directoryOffset + directoryBytes;
        if (directoryOffset < 0 ||
            directoryBytes < fixedHeaderBytes ||
            directoryEnd < directoryOffset ||
            directoryEnd > stream.Length)
            return false;

        var header = new byte[fixedHeaderBytes];
        stream.Position = directoryOffset;
        while (stream.Position < directoryEnd)
        {
            if (entryCount >= MaximumArchiveEntries ||
                directoryEnd - stream.Position < fixedHeaderBytes)
                return false;
            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) !=
                centralDirectoryHeaderSignature)
                return false;
            var fileNameBytes = BinaryPrimitives.ReadUInt16LittleEndian(
                header.AsSpan(28));
            var extraFieldBytes = BinaryPrimitives.ReadUInt16LittleEndian(
                header.AsSpan(30));
            var commentBytes = BinaryPrimitives.ReadUInt16LittleEndian(
                header.AsSpan(32));
            var variableBytes = (long)fileNameBytes + extraFieldBytes + commentBytes;
            if (variableBytes > directoryEnd - stream.Position) return false;
            stream.Position += variableBytes;
            entryCount++;
        }
        return stream.Position == directoryEnd && entryCount > 0;
    }

    internal static bool IsCandidateLengthAllowed(long length) =>
        length is > 0 and <= PioneerRxTop500ArtifactReadRequest.MaximumWorkbookBytes;

    internal static bool ArchiveShapeIsBounded(
        int entryCount,
        IEnumerable<long> uncompressedEntryLengths)
    {
        if (entryCount is <= 0 or > MaximumArchiveEntries) return false;
        long total = 0;
        foreach (var length in uncompressedEntryLengths)
        {
            if (length < 0 || length > MaximumUncompressedBytes) return false;
            if (total > MaximumUncompressedBytes - length) return false;
            total += length;
        }
        return total <= MaximumUncompressedBytes;
    }
}
