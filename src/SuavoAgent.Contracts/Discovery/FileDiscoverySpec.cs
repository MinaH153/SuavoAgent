namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Universal input to <c>FileLocatorService</c>. Describes what the
/// operator is looking for in vertical-neutral terms — the same record
/// works for pharmacy NDC lists, accounting QBO exports, legal PDFs,
/// restaurant POS CSVs, construction load tickets, laundromat payout
/// statements. Vertical-specific presets live in narrower packs that
/// construct this record with domain hints.
/// </summary>
/// <param name="Description">Human-readable intent, e.g. "top pricing list with one primary key per row."</param>
/// <param name="NameHints">Short concept keywords; the scorer substring-matches these against tokenized filenames. Min length 3 enforced.</param>
/// <param name="Extensions">Allowed file extensions (with leading dot). Empty list = any extension.</param>
/// <param name="Shape">Per-type expectation (tabular/document/email/…).</param>
/// <param name="RecentDaysBoost">Window over which recency decays linearly to zero. Default 90 days.</param>
/// <param name="MaxCandidates">Hard cap on candidates surfaced in the final result (best + alternatives).</param>
/// <param name="SourcePack">
/// Optional attribution: which <see cref="VerticalPack"/> supplied the
/// hints/extensions/shape in this spec. Used for audit trails + per-sector
/// learning feedback (the cloud learning worker aggregates observations
/// by pack id/version). Null when the spec was hand-assembled without a
/// pack. Does not drive runtime merging — the pack's data is already
/// materialized into the spec at construction time.
/// </param>
public sealed record FileDiscoverySpec(
    string Description,
    IReadOnlyList<string>? NameHints = null,
    IReadOnlyList<string>? Extensions = null,
    ShapeExpectation? Shape = null,
    int? RecentDaysBoost = 90,
    int MaxCandidates = 10,
    VerticalPack? SourcePack = null);
