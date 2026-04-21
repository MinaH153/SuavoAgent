using SuavoAgent.Contracts.Discovery;

namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Structured-JSON inference contract specific to file ranking. Narrower
/// than <see cref="Reasoning.ILocalInference"/> (which only emits
/// <c>RuleActionSpec</c> proposals for UI automation) and shaped around the
/// ranker's exact output schema so callers can't misuse it for free-text.
///
/// <para>
/// Implementations:
/// <list type="bullet">
///   <item><see cref="NullFileRankingInference"/> — always returns null;
///     used when Tier-2/3 are disabled per-pharmacy. The
///     <see cref="LlmFileRanker"/> falls back to its heuristic-only fallback
///     ranker in that case.</item>
///   <item><c>LocalSlmRankingInference</c> (session 4) — LLamaSharp-backed,
///     runs a 1–3B-param GGUF on-device. Grammar-constrained so the output
///     must be valid <see cref="FileRankingJudgment"/> JSON.</item>
///   <item><c>CloudClaudeRankingInference</c> (session 4) — signed POST to
///     <c>/api/agent/rank</c> fronting Anthropic. PHI-scrubbed inputs.</item>
/// </list>
/// </para>
///
/// <para>
/// Contract: never throws for inference failures. Null return = "escalate
/// to the next ranker tier." Only cancellation propagates.
/// </para>
/// </summary>
public interface IFileRankingInference
{
    /// <summary>
    /// Model id for audit trails — e.g. <c>"local-llama-3.2-3b-q4_k_m"</c>
    /// or <c>"cloud-claude-sonnet-4-6"</c>. <c>"none"</c> for the null
    /// implementation. Surfaced in <see cref="RankerVerdict"/>.<c>Reason</c>
    /// when the ranker attributes a decision to this inference tier.
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// Configured and reachable. Returns false when Tier-2/3 are disabled
    /// per-pharmacy or the model file isn't staged yet. Never blocks on
    /// actual model load — that happens lazily inside <see cref="RankAsync"/>.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Which tier this implementation represents. Carried through to
    /// <see cref="RankerVerdict.Tier"/> for audit attribution.
    /// </summary>
    RankerTier Tier { get; }

    /// <summary>
    /// Returns a single judgment picking one candidate from the list with
    /// a confidence and reason. Null on timeout/parse-failure/grammar-miss
    /// — caller escalates. Grammar or JSON-mode MUST constrain the output
    /// to exactly match <see cref="FileRankingJudgment"/>.
    /// </summary>
    Task<FileRankingJudgment?> RankAsync(
        FileDiscoverySpec spec,
        IReadOnlyList<FileCandidateForRanker> candidates,
        CancellationToken ct);
}

/// <summary>
/// The constrained output of <see cref="IFileRankingInference.RankAsync"/>.
/// </summary>
/// <param name="PickedCandidateId">One of the input <c>CandidateId</c>s.</param>
/// <param name="Confidence">In [0,1]. The LLM's confidence in its pick.</param>
/// <param name="Reason">One-line human-readable rationale. Must not contain PHI.</param>
public sealed record FileRankingJudgment(
    string PickedCandidateId,
    double Confidence,
    string Reason);
