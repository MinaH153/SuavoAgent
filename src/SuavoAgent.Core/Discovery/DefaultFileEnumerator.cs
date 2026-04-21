using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Default <see cref="IFileEnumerator"/>. Walks the user's Desktop,
/// Downloads, Documents, OneDrive and Dropbox roots top-level only (no
/// deep recursion) — we trust the bucket + filename signals for the
/// first-pass ranker. Excel-MRU and Windows-Recent extraction ship as a
/// separate Windows-only enumerator later.
///
/// <para>
/// Fault-tolerant: access-denied, missing-directory, and IO errors log
/// and are skipped per-file, not per-root, so a single locked item can't
/// abort the whole pass. An aggregate error-count is logged at debug
/// level; when that count is high (indicating a deeper disk or
/// permissions problem) it promotes to warning.
/// </para>
///
/// <para>
/// Dedupe: if two roots resolve to the same absolute path (common when
/// Desktop is a OneDrive-synced folder), we only yield the first
/// candidate per canonical path — keeping the bucket whose prior is
/// higher.
/// </para>
/// </summary>
public sealed class DefaultFileEnumerator : IFileEnumerator
{
    private readonly IReadOnlyList<EnumerationRoot> _roots;
    private readonly ILogger<DefaultFileEnumerator>? _logger;

    public DefaultFileEnumerator(
        IReadOnlyList<EnumerationRoot>? roots = null,
        ILogger<DefaultFileEnumerator>? logger = null)
    {
        _roots = roots ?? BuildDefaultRoots();
        _logger = logger;
    }

    public IReadOnlyList<FileCandidate> Enumerate(
        FileDiscoverySpec spec,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        var extensions = NormalizeExtensions(spec.Extensions);
        var byPath = new Dictionary<string, FileCandidate>(StringComparer.OrdinalIgnoreCase);
        int ioErrors = 0;

        foreach (var root in _roots)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(root.Path) || !Directory.Exists(root.Path)) continue;

            IEnumerable<string>? files = null;
            try
            {
                files = Directory.EnumerateFiles(root.Path, "*", SearchOption.TopDirectoryOnly);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException ex)
            {
                ioErrors++;
                _logger?.LogDebug(ex, "Enumerator skipped root {Path}", root.Path);
                continue;
            }

            using var enumerator = files.GetEnumerator();
            while (true)
            {
                if (ct.IsCancellationRequested) break;

                bool hasNext;
                try
                {
                    hasNext = enumerator.MoveNext();
                }
                catch (UnauthorizedAccessException) { ioErrors++; break; }
                catch (DirectoryNotFoundException) { ioErrors++; break; }
                catch (IOException ex)
                {
                    ioErrors++;
                    _logger?.LogDebug(ex, "Enumerator iteration halted in {Path}", root.Path);
                    break;
                }
                if (!hasNext) break;

                var path = enumerator.Current;
                if (!TryBuildCandidate(path, root.Bucket, out var candidate, ref ioErrors)) continue;
                if (candidate is null) continue;

                if (extensions is not null &&
                    !extensions.Contains(Path.GetExtension(candidate.FileName))) continue;

                var canonical = candidate.AbsolutePath;
                if (byPath.TryGetValue(canonical, out var existing))
                {
                    // Same file seen under two roots — keep the higher-prior bucket.
                    if (BucketPriority(candidate.Bucket) > BucketPriority(existing.Bucket))
                    {
                        byPath[canonical] = candidate;
                    }
                }
                else
                {
                    byPath[canonical] = candidate;
                }
            }
        }

        if (ioErrors >= 5)
        {
            _logger?.LogWarning(
                "Enumerator encountered {Errors} I/O errors across {Roots} roots — "
                + "disk or permissions may be degraded", ioErrors, _roots.Count);
        }
        _logger?.LogDebug(
            "Enumerator yielded {Count} candidates ({Errors} errors)",
            byPath.Count, ioErrors);

        return byPath.Values.ToList();
    }

    private static bool TryBuildCandidate(
        string path,
        FileLocationBucket bucket,
        out FileCandidate? candidate,
        ref int ioErrors)
    {
        candidate = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return true; // race: file vanished; skip quietly

            candidate = new FileCandidate(
                AbsolutePath: info.FullName,
                FileName: info.Name,
                SizeBytes: info.Length,
                LastModifiedUtc: info.LastWriteTimeUtc,
                Bucket: bucket,
                HeuristicScore: 0.0,
                LastOpenedUtc: null);
            return true;
        }
        catch (UnauthorizedAccessException) { ioErrors++; return true; }
        catch (FileNotFoundException) { return true; }
        catch (IOException) { ioErrors++; return true; }
    }

    // Priors used only when two roots surface the same file — kept local
    // to the enumerator so the scorer's priors aren't duplicated here.
    private static int BucketPriority(FileLocationBucket bucket) => bucket switch
    {
        FileLocationBucket.ExcelMru => 100,
        FileLocationBucket.WindowsRecent => 90,
        FileLocationBucket.Desktop => 85,
        FileLocationBucket.Downloads => 80,
        FileLocationBucket.OneDrive => 65,
        FileLocationBucket.Dropbox => 65,
        FileLocationBucket.Documents => 55,
        _ => 30,
    };

    private static IReadOnlyList<EnumerationRoot> BuildDefaultRoots()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var roots = new List<EnumerationRoot>();
        if (!string.IsNullOrEmpty(desktop))
            roots.Add(new EnumerationRoot(desktop, FileLocationBucket.Desktop));
        if (!string.IsNullOrEmpty(user))
        {
            roots.Add(new EnumerationRoot(Path.Combine(user, "Downloads"), FileLocationBucket.Downloads));
            roots.Add(new EnumerationRoot(Path.Combine(user, "OneDrive"), FileLocationBucket.OneDrive));
            roots.Add(new EnumerationRoot(Path.Combine(user, "Dropbox"), FileLocationBucket.Dropbox));
        }
        if (!string.IsNullOrEmpty(documents))
            roots.Add(new EnumerationRoot(documents, FileLocationBucket.Documents));
        return roots;
    }

    private static HashSet<string>? NormalizeExtensions(IReadOnlyList<string>? extensions)
    {
        if (extensions is null) return null;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in extensions)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            var trimmed = e.Trim();
            if (!trimmed.StartsWith('.')) trimmed = "." + trimmed;
            set.Add(trimmed);
        }
        return set.Count == 0 ? null : set;
    }
}
