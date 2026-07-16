using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Helper.Workflows;

public sealed class PioneerRxPricedWorkbookStore : IDisposable
{
    private static readonly TimeSpan PartialUploadRetention = TimeSpan.FromHours(24);
    private static readonly TimeSpan LiveSessionRetention = TimeSpan.FromHours(1);
    private const int MaximumConcurrentUploads = 8;

    private sealed record UploadSession(
        string JobId,
        string UploadToken,
        string ExpectedSha256,
        long ExpectedBytes,
        string TemporaryPath,
        DateTimeOffset CreatedAtUtc,
        SemaphoreSlim Gate);

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly string _documentsDirectory;
    private readonly string _artifactDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, UploadSession> _sessions =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _beginGate = new(1, 1);

    public PioneerRxPricedWorkbookStore(
        string documentsDirectory,
        TimeProvider? timeProvider = null)
    {
        _documentsDirectory = Path.GetFullPath(
            documentsDirectory ?? throw new ArgumentNullException(nameof(documentsDirectory)));
        _artifactDirectory = Path.Combine(_documentsDirectory, "SuavoAgent Reports");
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<PioneerRxPricedWorkbookBeginResult> BeginAsync(
        PioneerRxPricedWorkbookBeginRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid())
            return PioneerRxPricedWorkbookBeginResult.Failed(
                request.JobId,
                PioneerRxPricedWorkbookPublicationCodes.InvalidRequest);
        if (!TryPrepareDestination())
            return PioneerRxPricedWorkbookBeginResult.Failed(
                request.JobId,
                PioneerRxPricedWorkbookPublicationCodes.DestinationUnavailable);

        var finalPath = FinalPath(request.JobId);
        if (File.Exists(finalPath))
        {
            var verified = await VerifyPublishedAsync(
                finalPath,
                request.WorkbookSha256,
                request.WorkbookBytes,
                ct).ConfigureAwait(false);
            return verified
                ? new PioneerRxPricedWorkbookBeginResult(
                    PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                    request.JobId,
                    true,
                    PioneerRxPricedWorkbookPublicationCodes.Published,
                    null,
                    true,
                    request.WorkbookBytes,
                    PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
                    request.WorkbookSha256,
                    request.WorkbookBytes)
                : PioneerRxPricedWorkbookBeginResult.Failed(
                    request.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.PublicationCollision);
        }

        await _beginGate.WaitAsync(ct).ConfigureAwait(false);
        string? orphanedTemporaryPath = null;
        try
        {
            CleanupExpiredSessions();
            if (File.Exists(finalPath))
            {
                var verified = await VerifyPublishedAsync(
                    finalPath,
                    request.WorkbookSha256,
                    request.WorkbookBytes,
                    ct).ConfigureAwait(false);
                return verified
                    ? new PioneerRxPricedWorkbookBeginResult(
                        PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                        request.JobId,
                        true,
                        PioneerRxPricedWorkbookPublicationCodes.Published,
                        null,
                        true,
                        request.WorkbookBytes,
                        PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
                        request.WorkbookSha256,
                        request.WorkbookBytes)
                    : PioneerRxPricedWorkbookBeginResult.Failed(
                        request.JobId,
                        PioneerRxPricedWorkbookPublicationCodes.PublicationCollision);
            }
            if (_sessions.TryGetValue(request.JobId, out var existing))
            {
                if (!SessionMatches(
                        existing,
                        existing.UploadToken,
                        request.WorkbookSha256,
                        request.WorkbookBytes) ||
                    !IsSafeTemporaryFile(existing.TemporaryPath))
                    return PioneerRxPricedWorkbookBeginResult.Failed(
                        request.JobId,
                        PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);
                var nextOffset = new FileInfo(existing.TemporaryPath).Length;
                if (nextOffset < 0 || nextOffset > request.WorkbookBytes)
                    return PioneerRxPricedWorkbookBeginResult.Failed(
                        request.JobId,
                        PioneerRxPricedWorkbookPublicationCodes.IntegrityMismatch);
                return new PioneerRxPricedWorkbookBeginResult(
                    PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                    request.JobId,
                    true,
                    PioneerRxPricedWorkbookPublicationCodes.UploadReady,
                    existing.UploadToken,
                    false,
                    nextOffset,
                    PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
                    request.WorkbookSha256,
                    request.WorkbookBytes);
            }

            if (_sessions.Count >= MaximumConcurrentUploads)
                return PioneerRxPricedWorkbookBeginResult.Failed(
                    request.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);

            var uploadToken = Guid.NewGuid().ToString("N");
            var temporaryPath = Path.Combine(
                _artifactDirectory,
                $".SuavoAgent-Top-500-Priced-{request.JobId}-{uploadToken}.partial.xlsx");
            orphanedTemporaryPath = temporaryPath;
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            var session = new UploadSession(
                request.JobId,
                uploadToken,
                request.WorkbookSha256,
                request.WorkbookBytes,
                temporaryPath,
                _timeProvider.GetUtcNow(),
                new SemaphoreSlim(1, 1));
            if (!_sessions.TryAdd(request.JobId, session))
            {
                session.Gate.Dispose();
                TryDelete(temporaryPath);
                orphanedTemporaryPath = null;
                return PioneerRxPricedWorkbookBeginResult.Failed(
                    request.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);
            }
            orphanedTemporaryPath = null;
            return new PioneerRxPricedWorkbookBeginResult(
                PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                request.JobId,
                true,
                PioneerRxPricedWorkbookPublicationCodes.UploadReady,
                uploadToken,
                false,
                0,
                PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
                request.WorkbookSha256,
                request.WorkbookBytes);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            if (orphanedTemporaryPath is not null)
                TryDelete(orphanedTemporaryPath);
            return PioneerRxPricedWorkbookBeginResult.Failed(
                request.JobId,
                PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);
        }
        finally
        {
            _beginGate.Release();
        }
    }

    public async Task<PioneerRxPricedWorkbookChunkResult> AppendAsync(
        PioneerRxPricedWorkbookChunkRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid() ||
            !_sessions.TryGetValue(request.JobId, out var session) ||
            !SessionMatches(session, request.UploadToken, request.ExpectedSha256,
                request.ExpectedBytes))
            return PioneerRxPricedWorkbookChunkResult.Failed(
                request.JobId,
                PioneerRxPricedWorkbookPublicationCodes.InvalidRequest);

        byte[] chunk;
        try
        {
            chunk = Convert.FromBase64String(request.ChunkBase64);
        }
        catch
        {
            return PioneerRxPricedWorkbookChunkResult.Failed(
                request.JobId,
                PioneerRxPricedWorkbookPublicationCodes.InvalidRequest);
        }
        if (chunk.Length is <= 0 or >
                PioneerRxPricedWorkbookPublicationContract.MaximumChunkBytes ||
            request.Offset + chunk.LongLength > request.ExpectedBytes)
            return PioneerRxPricedWorkbookChunkResult.Failed(
                request.JobId,
                PioneerRxPricedWorkbookPublicationCodes.InvalidRequest);

        await session.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsSafeTemporaryFile(session.TemporaryPath))
                return PioneerRxPricedWorkbookChunkResult.Failed(
                    request.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);
            await using var stream = new FileStream(
                session.TemporaryPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            if (stream.Length != request.Offset)
                return PioneerRxPricedWorkbookChunkResult.Failed(
                    request.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.IntegrityMismatch);
            stream.Position = request.Offset;
            await stream.WriteAsync(chunk, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return new PioneerRxPricedWorkbookChunkResult(
                PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                request.JobId,
                true,
                PioneerRxPricedWorkbookPublicationCodes.ChunkAccepted,
                stream.Length);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return PioneerRxPricedWorkbookChunkResult.Failed(
                request.JobId,
                PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);
        }
        finally
        {
            session.Gate.Release();
            CryptographicOperations.ZeroMemory(chunk);
        }
    }

    public async Task<PioneerRxPricedWorkbookCommitResult> CommitAsync(
        PioneerRxPricedWorkbookCommitRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsValid() ||
            !_sessions.TryGetValue(request.JobId, out var session) ||
            !SessionMatches(session, request.UploadToken, request.ExpectedSha256,
                request.ExpectedBytes))
            return PioneerRxPricedWorkbookCommitResult.Failed(
                request.JobId,
                PioneerRxPricedWorkbookPublicationCodes.InvalidRequest);

        await session.Gate.WaitAsync(ct).ConfigureAwait(false);
        var removeSession = false;
        try
        {
            if (!IsSafeTemporaryFile(session.TemporaryPath))
                return PioneerRxPricedWorkbookCommitResult.Failed(
                    request.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.UploadUnavailable);
            var info = new FileInfo(session.TemporaryPath);
            if (info.Length != request.ExpectedBytes)
                return PioneerRxPricedWorkbookCommitResult.Failed(
                    request.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.IntegrityMismatch);

            var digest = await HashAsync(session.TemporaryPath, ct).ConfigureAwait(false);
            if (!string.Equals(digest, request.ExpectedSha256, StringComparison.Ordinal))
            {
                removeSession = true;
                return PioneerRxPricedWorkbookCommitResult.Failed(
                    request.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.IntegrityMismatch);
            }
            if (!HasExactSchema(session.TemporaryPath))
            {
                removeSession = true;
                return PioneerRxPricedWorkbookCommitResult.Failed(
                    request.JobId,
                    PioneerRxPricedWorkbookPublicationCodes.SchemaMismatch);
            }

            var finalPath = FinalPath(request.JobId);
            try
            {
                File.Move(session.TemporaryPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                if (!await VerifyPublishedAsync(
                        finalPath,
                        request.ExpectedSha256,
                        request.ExpectedBytes,
                        ct).ConfigureAwait(false))
                    return PioneerRxPricedWorkbookCommitResult.Failed(
                        request.JobId,
                        PioneerRxPricedWorkbookPublicationCodes.PublicationCollision);
                TryDelete(session.TemporaryPath);
            }
            removeSession = true;
            return new PioneerRxPricedWorkbookCommitResult(
                PioneerRxPricedWorkbookPublicationContract.CurrentVersion,
                request.JobId,
                true,
                PioneerRxPricedWorkbookPublicationCodes.Published,
                PioneerRxPricedWorkbookPublicationContract.DestinationLabel,
                request.ExpectedSha256,
                request.ExpectedBytes,
                PioneerRxPricedWorkbookPublicationContract.ExpectedDataRows);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            removeSession = true;
            return PioneerRxPricedWorkbookCommitResult.Failed(
                request.JobId,
                PioneerRxPricedWorkbookPublicationCodes.PublicationFailed);
        }
        finally
        {
            session.Gate.Release();
            if (removeSession) RemoveSession(request.JobId);
        }
    }

    internal bool TryResolvePublishedPath(string jobId, out string? path)
    {
        path = null;
        if (!PioneerRxPricedWorkbookPublicationContract.IsCanonicalJobId(jobId))
            return false;
        try
        {
            var candidate = FinalPath(jobId);
            if (!File.Exists(candidate) ||
                (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                return false;
            path = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryPrepareDestination()
    {
        try
        {
            if (!Directory.Exists(_documentsDirectory) ||
                (File.GetAttributes(_documentsDirectory) & FileAttributes.ReparsePoint) != 0)
                return false;
            Directory.CreateDirectory(_artifactDirectory);
            var normalized = Path.GetFullPath(_artifactDirectory);
            var safe = PathComparer.Equals(
                           Path.GetDirectoryName(normalized),
                           _documentsDirectory) &&
                       (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) == 0;
            if (safe) CleanupExpiredPartialUploads();
            return safe;
        }
        catch
        {
            return false;
        }
    }

    private string FinalPath(string jobId) => Path.Combine(
        _artifactDirectory,
        $"SuavoAgent-Top-500-Priced-{jobId}.xlsx");

    private bool IsSafeTemporaryFile(string path)
    {
        try
        {
            var normalized = Path.GetFullPath(path);
            return File.Exists(normalized) &&
                   PathComparer.Equals(Path.GetDirectoryName(normalized), _artifactDirectory) &&
                   (File.GetAttributes(normalized) & FileAttributes.ReparsePoint) == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SessionMatches(
        UploadSession session,
        string token,
        string sha256,
        long length) =>
        string.Equals(session.UploadToken, token, StringComparison.Ordinal) &&
        string.Equals(session.ExpectedSha256, sha256, StringComparison.Ordinal) &&
        session.ExpectedBytes == length;

    private void CleanupExpiredPartialUploads()
    {
        var active = _sessions.Values
            .Select(session => session.TemporaryPath)
            .ToFrozenSet(PathComparer);
        var cutoff = DateTime.UtcNow - PartialUploadRetention;
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFiles(
                    _artifactDirectory,
                    ".SuavoAgent-Top-500-Priced-*.partial.xlsx",
                    SearchOption.TopDirectoryOnly)
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
                if (active.Contains(fullPath) ||
                    !PathComparer.Equals(Path.GetDirectoryName(fullPath), _artifactDirectory) ||
                    !IsManagedPartialUploadName(Path.GetFileName(fullPath)) ||
                    (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                    File.GetLastWriteTimeUtc(fullPath) > cutoff)
                    continue;
                File.Delete(fullPath);
            }
            catch
            {
                // A locked or racing upload is retried on the next begin.
            }
        }
    }

    private void CleanupExpiredSessions()
    {
        var cutoff = _timeProvider.GetUtcNow() - LiveSessionRetention;
        foreach (var pair in _sessions.ToArray())
        {
            var session = pair.Value;
            if (session.CreatedAtUtc > cutoff || !session.Gate.Wait(0)) continue;
            try
            {
                if (_sessions.TryGetValue(pair.Key, out var current) &&
                    ReferenceEquals(current, session) &&
                    _sessions.TryRemove(pair.Key, out var removed) &&
                    ReferenceEquals(removed, session))
                    TryDelete(session.TemporaryPath);
            }
            finally
            {
                session.Gate.Release();
            }
        }
    }

    private static bool IsManagedPartialUploadName(string name)
    {
        const string prefix = ".SuavoAgent-Top-500-Priced-";
        const string suffix = ".partial.xlsx";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var payload = name[prefix.Length..^suffix.Length];
        if (payload.Length != 69 || payload[36] != '-') return false;
        return Guid.TryParseExact(payload[..36], "D", out _) &&
               PioneerRxPricedWorkbookPublicationContract.IsLowerHex(
                   payload[37..],
                   32);
    }

    private static bool HasExactSchema(string path)
        => PioneerRxPricedWorkbookSchemaValidator.IsExact(path);

    private static async Task<bool> VerifyPublishedAsync(
        string path,
        string sha256,
        long length,
        CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists ||
                info.Length != length ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !HasExactSchema(path))
                return false;
            return string.Equals(
                await HashAsync(path, ct).ConfigureAwait(false),
                sha256,
                StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        try
        {
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private void RemoveSession(string jobId)
    {
        if (!_sessions.TryRemove(jobId, out var session)) return;
        TryDelete(session.TemporaryPath);
        session.Gate.Dispose();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        foreach (var jobId in _sessions.Keys.ToArray()) RemoveSession(jobId);
        _beginGate.Dispose();
    }
}
