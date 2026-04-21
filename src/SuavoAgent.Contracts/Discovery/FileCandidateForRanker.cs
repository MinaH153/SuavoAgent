namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Privacy-safe projection of a <see cref="FileCandidateSample"/> for the
/// LLM ranker tier. Raw filenames, absolute paths, and error strings do NOT
/// cross into this record — only bands, counts, and PHI-scrubbed header
/// tokens. The locator constructs this via the scrubber before any LLM
/// (local SLM or cloud) sees candidate metadata, which enforces the
/// privacy boundary at the type system instead of relying on every caller.
///
/// <para>
/// What is intentionally missing: <c>AbsolutePath</c>, <c>FileName</c>,
/// <c>ErrorMessage</c>. If any of those are needed for operator display,
/// they come from the raw <see cref="FileCandidate"/> / <c>FileCandidateSample</c>
/// the portal renders — never the ranker's input.
/// </para>
/// </summary>
/// <param name="CandidateId">
/// Stable identifier the locator uses to correlate ranker outputs back to
/// the raw <see cref="FileCandidateSample"/>. Opaque to the ranker; never
/// a real path.
/// </param>
/// <param name="DirectoryDepth">Path depth below the user's home directory.</param>
/// <param name="Size">Coarse size band, not raw bytes.</param>
/// <param name="Recency">Coarse recency band relative to now.</param>
/// <param name="Extension">File extension with leading dot. Not PHI.</param>
/// <param name="Bucket">Where on disk the candidate was found.</param>
/// <param name="ColumnHeaderCount">Number of columns (counts are not PHI).</param>
/// <param name="ScrubbedColumnHeaders">
/// Column headers after passing through the PHI scrubber. Tokens that
/// looked like patient identifiers, names, or other PHI are replaced with
/// <c>&lt;PII&gt;</c>. Structural headers like "NDC", "Cost", "Supplier"
/// pass through unchanged.
/// </param>
/// <param name="RowCount">Row count. Not PHI.</param>
/// <param name="HasPrimaryKeyShape">
/// Whether any column matched the spec's <c>ExpectedColumnPattern</c>
/// (e.g., NDC regex for pharmacy). Boolean signal only.
/// </param>
/// <param name="StructureMatchesHints">Whether headers match spec hints.</param>
/// <param name="HeuristicScore">Deterministic Tier-1 score for cross-reference.</param>
/// <param name="SampleOutcome">
/// Whether the sampler successfully read the file's shape. The LLM tier
/// needs to distinguish "unreadable" (ignore boosts, cap confidence)
/// from "readable but no primary-key evidence" (legitimate low signal).
/// Without this bit both paths look identical to the ranker.
/// </param>
public sealed record FileCandidateForRanker(
    string CandidateId,
    int DirectoryDepth,
    SizeBand Size,
    RecencyBand Recency,
    string Extension,
    FileLocationBucket Bucket,
    int ColumnHeaderCount,
    IReadOnlyList<string> ScrubbedColumnHeaders,
    int RowCount,
    bool HasPrimaryKeyShape,
    bool StructureMatchesHints,
    double HeuristicScore,
    SampleOutcome SampleOutcome);

/// <summary>
/// Result of attempting to sample a candidate's contents. Distinguishes
/// three failure modes that the ranker must treat differently.
/// </summary>
public enum SampleOutcome
{
    /// <summary>Sample read cleanly; structure fields are authoritative.</summary>
    Sampled,

    /// <summary>Sampler attempted but failed (I/O error, corrupt file, cancelled).
    /// Structure fields are defaults — ranker must NOT auto-use on filename alone.</summary>
    SampleFailed,

    /// <summary>Candidate was outside the sampled top-K — we only have heuristic
    /// signals, not content-verified structure. Ranker should prefer Sampled
    /// candidates over NotSampled when confidences tie.</summary>
    NotSampled,
}

/// <summary>Coarse size bands, chosen so typical tabular working files
/// land cleanly in <c>Small</c>/<c>Medium</c>.</summary>
public enum SizeBand
{
    /// <summary>&lt; 10 KB — likely empty, header-only, or corrupt.</summary>
    Tiny,
    /// <summary>10 KB – 500 KB — single-sheet working file.</summary>
    Small,
    /// <summary>500 KB – 5 MB — large working file with formatting/formulas.</summary>
    Medium,
    /// <summary>5 MB – 50 MB — multi-sheet or image-embedded.</summary>
    Large,
    /// <summary>&gt; 50 MB — archive/bulk; rarely the right target.</summary>
    Huge,
}

/// <summary>Coarse recency bands, chosen to match how operators reason
/// about their own files ("today", "this week", "old").</summary>
public enum RecencyBand
{
    /// <summary>Modified within the last 24 hours.</summary>
    Today,
    /// <summary>Modified within the last 7 days.</summary>
    ThisWeek,
    /// <summary>Modified within the last 30 days.</summary>
    ThisMonth,
    /// <summary>Modified within the last 90 days.</summary>
    ThisQuarter,
    /// <summary>Older than 90 days.</summary>
    Older,
}
