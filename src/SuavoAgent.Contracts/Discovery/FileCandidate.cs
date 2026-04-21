namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// A file found on disk that might match the discovery spec, pre-sample.
/// <c>HeuristicScore</c> is cheap + deterministic (filename/recency/extension/
/// path) and is populated by <c>IFilenameHeuristicScorer</c>. No file
/// contents have been read at this stage.
///
/// <c>AbsolutePath</c> and <c>FileName</c> are operator-visible (portal
/// displays them) but MUST NOT be forwarded to an off-device LLM ranker
/// directly — use <c>FileCandidateForRanker</c> for the scrubbed, LLM-safe
/// projection.
/// </summary>
/// <param name="LastOpenedUtc">
/// Optional: when the OS last recorded an "opened" event for this file
/// (Excel MRU, Windows Recent .lnk, or Explorer recent-items). Null when
/// the enumerator couldn't source a reliable signal. The scorer uses this
/// to decay the <see cref="FileLocationBucket.ExcelMru"/> prior — a stale
/// MRU entry shouldn't rank as high as a freshly-opened one.
/// </param>
public sealed record FileCandidate(
    string AbsolutePath,
    string FileName,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    FileLocationBucket Bucket,
    double HeuristicScore,
    DateTimeOffset? LastOpenedUtc = null);

/// <summary>
/// Where on disk a candidate was found. Priors-informed: Desktop/Downloads
/// rank above deep Documents paths because that's where humans stash
/// in-progress work. Excel's own MRU list is treated as near-gold.
/// </summary>
public enum FileLocationBucket
{
    Desktop,
    Downloads,
    Documents,
    OneDrive,
    Dropbox,
    ExcelMru,
    WindowsRecent,
    Other,
}
