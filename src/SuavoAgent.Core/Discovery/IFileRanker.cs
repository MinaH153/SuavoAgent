using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Produces the final ranked list of candidates from the privacy-safe
/// <see cref="FileCandidateForRanker"/> projection. The locator owns the
/// mapping between <see cref="RankerVerdict.CandidateId"/> and the raw
/// <see cref="FileCandidateSample"/> it came from — rankers never see
/// raw filenames or paths, which enforces the privacy boundary by type.
///
/// <para>
/// Implementations layer on top of one another:
/// <list type="bullet">
///   <item><see cref="HeuristicOnlyRanker"/> — deterministic, always
///     available, used as the Tier-1 fallback.</item>
///   <item><c>SlmFileRanker</c> (session 3) — local SLM via LLamaSharp;
///     consulted when heuristic confidence is ambiguous.</item>
///   <item><c>CloudRanker</c> (session 3) — Anthropic Claude via our
///     signed <c>/api/agent/reason</c>; only when the SLM escalates.</item>
/// </list>
/// </para>
/// </summary>
public interface IFileRanker
{
    Task<IReadOnlyList<RankerVerdict>> RankAsync(
        FileDiscoverySpec spec,
        IReadOnlyList<FileCandidateForRanker> candidates,
        CancellationToken ct);
}
