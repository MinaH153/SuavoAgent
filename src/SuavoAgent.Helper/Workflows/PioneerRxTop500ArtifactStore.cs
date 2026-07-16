using System.Security.Cryptography;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper.Workflows;

internal sealed record PublishedTop500Artifact(
    string Token,
    string Sha256,
    long Length);

internal sealed record ReadTop500Artifact(
    byte[] Bytes,
    long Offset,
    long NextOffset,
    bool Complete,
    string Sha256,
    long Length);

/// <summary>
/// Promotes a verified PioneerRx download into protected Helper-local staging.
/// Only an opaque token leaves this store; the path remains Helper-local. The
/// raw PioneerRx workbook is never presented as the user's final deliverable.
/// </summary>
internal sealed class PioneerRxTop500ArtifactStore
{
    private const int ChunkBytes = 24 * 1024;
    private static readonly TimeSpan ArtifactRetention = TimeSpan.FromHours(24);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _documentsDirectory;
    private readonly string _artifactDirectory;

    public PioneerRxTop500ArtifactStore(string documentsDirectory)
        : this(documentsDirectory, "SuavoAgent Reports")
    {
    }

    internal PioneerRxTop500ArtifactStore(
        string rootDirectory,
        string artifactSubdirectory)
    {
        _documentsDirectory = Path.GetFullPath(
            rootDirectory ?? throw new ArgumentNullException(nameof(rootDirectory)));
        if (string.IsNullOrWhiteSpace(artifactSubdirectory) ||
            Path.IsPathRooted(artifactSubdirectory))
            throw new ArgumentException(
                "Artifact subdirectory must be relative.",
                nameof(artifactSubdirectory));
        _artifactDirectory = Path.Combine(_documentsDirectory, artifactSubdirectory);
    }

    public static string ResolveDefaultDocumentsDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static string ResolveDefaultStagingRootDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public bool TryPrepare()
    {
        try
        {
            if (!Directory.Exists(_documentsDirectory) ||
                (File.GetAttributes(_documentsDirectory) & FileAttributes.ReparsePoint) != 0)
                return false;
            Directory.CreateDirectory(_artifactDirectory);
            var normalized = Path.GetFullPath(_artifactDirectory);
            var relative = Path.GetRelativePath(_documentsDirectory, normalized);
            var safe = !Path.IsPathRooted(relative) &&
                       !relative.Equals("..", StringComparison.Ordinal) &&
                       !relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                           StringComparison.Ordinal) &&
                       !ContainsReparsePoint(_documentsDirectory, normalized);
            if (safe) CleanupExpiredArtifacts();
            return safe;
        }
        catch
        {
            return false;
        }
    }

    public async Task<PublishedTop500Artifact?> PublishAsync(
        StableXlsxExport source,
        DateOnly runDate,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!TryPrepare()) return null;

        var token = Guid.NewGuid().ToString("N");
        var stem = $"SuavoAgent-Top-500-{runDate:yyyyMMdd}-{token}";
        var temporaryPath = Path.Combine(_artifactDirectory, $".{stem}.partial");
        var finalPath = Path.Combine(_artifactDirectory, $"{stem}.xlsx");
        try
        {
            await using (var input = new FileStream(
                source.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            var published = await ValidateCopiedFileAsync(
                temporaryPath,
                source,
                token,
                ct).ConfigureAwait(false);
            if (published is null) return null;

            File.Move(temporaryPath, finalPath, overwrite: false);
            TryDeleteSource(source.FullPath);
            return published;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch
        {
            TryDelete(temporaryPath);
            return null;
        }
    }

    internal bool TryResolveToken(string token, out string? fullPath)
    {
        fullPath = null;
        if (!IsToken(token) || !Directory.Exists(_artifactDirectory)) return false;
        try
        {
            var matches = Directory.EnumerateFiles(
                    _artifactDirectory,
                    $"SuavoAgent-Top-500-*-{token}.xlsx",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFullPath)
                .Where(path => PathComparer.Equals(Path.GetDirectoryName(path), _artifactDirectory))
                .Take(2)
                .ToArray();
            if (matches.Length != 1) return false;
            if ((File.GetAttributes(matches[0]) & FileAttributes.ReparsePoint) != 0) return false;
            fullPath = matches[0];
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ReadTop500Artifact?> ReadAsync(
        PioneerRxTop500ArtifactReadRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid() ||
            !TryResolveToken(request.ArtifactToken, out var path) ||
            path is null)
            return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists ||
                info.Length != request.ExpectedBytes ||
                info.Length > PioneerRxTop500ArtifactReadRequest.MaximumWorkbookBytes ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Position = request.Offset;
            var count = checked((int)Math.Min(ChunkBytes, info.Length - request.Offset));
            var bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            var nextOffset = request.Offset + count;

            var after = new FileInfo(path);
            return after.Exists &&
                   after.Length == info.Length &&
                   after.LastWriteTimeUtc == info.LastWriteTimeUtc
                ? new ReadTop500Artifact(
                    bytes,
                    request.Offset,
                    nextOffset,
                    nextOffset == info.Length,
                    request.ExpectedSha256,
                    info.Length)
                : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<PublishedTop500Artifact?> ValidateCopiedFileAsync(
        string path,
        StableXlsxExport source,
        string token,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != source.Length) return null;
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(hash).ToLowerInvariant();
        return string.Equals(sha256, source.Sha256, StringComparison.Ordinal)
            ? new PublishedTop500Artifact(token, sha256, source.Length)
            : null;
    }

    private static bool IsToken(string token) =>
        token is { Length: 32 } && token.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private void CleanupExpiredArtifacts()
    {
        var cutoff = DateTime.UtcNow - ArtifactRetention;
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(
                    _artifactDirectory,
                    "SuavoAgent-Top-500-*",
                    SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(
                    _artifactDirectory,
                    ".SuavoAgent-Top-500-*",
                    SearchOption.TopDirectoryOnly))
                .ToArray();
        }
        catch
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            try
            {
                var fullPath = Path.GetFullPath(candidate);
                if (!PathComparer.Equals(Path.GetDirectoryName(fullPath), _artifactDirectory) ||
                    !IsManagedRawArtifactName(Path.GetFileName(fullPath)) ||
                    (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                    File.GetLastWriteTimeUtc(fullPath) > cutoff)
                    continue;
                File.Delete(fullPath);
            }
            catch
            {
                // A locked or racing artifact remains available for retry and
                // will be considered again on the next workflow preparation.
            }
        }
    }

    private static bool IsManagedRawArtifactName(string name)
    {
        const string finalPrefix = "SuavoAgent-Top-500-";
        const string finalSuffix = ".xlsx";
        const string partialPrefix = ".SuavoAgent-Top-500-";
        const string partialSuffix = ".partial";
        var payload = name.StartsWith(finalPrefix, StringComparison.Ordinal) &&
                      name.EndsWith(finalSuffix, StringComparison.Ordinal)
            ? name[finalPrefix.Length..^finalSuffix.Length]
            : name.StartsWith(partialPrefix, StringComparison.Ordinal) &&
              name.EndsWith(partialSuffix, StringComparison.Ordinal)
                ? name[partialPrefix.Length..^partialSuffix.Length]
                : string.Empty;
        return payload.Length == 41 &&
               payload[8] == '-' &&
               payload[..8].All(char.IsAsciiDigit) &&
               IsToken(payload[9..]);
    }

    private static bool ContainsReparsePoint(string root, string leaf)
    {
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        for (var current = new DirectoryInfo(leaf);
             current is not null;
             current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            if (PathComparer.Equals(
                    current.FullName.TrimEnd(Path.DirectorySeparatorChar),
                    rootPath))
                return false;
        }
        return true;
    }

    private static void TryDeleteSource(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The verified staged copy is authoritative. A locked source may
            // remain in Downloads; it is never returned as the artifact.
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
