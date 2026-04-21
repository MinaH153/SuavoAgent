namespace SuavoAgent.Core.Discovery;

/// <summary>
/// Tunables for <see cref="FileLocatorService"/>. Confidence bands
/// determine whether the top candidate auto-uses or requires portal
/// confirmation. <c>SampleDepth</c> bounds how many files the sampler
/// opens (the expensive step); the final result size comes from
/// <c>FileDiscoverySpec.MaxCandidates</c>, not from here.
/// </summary>
public sealed class FileLocatorOptions
{
    /// <summary>Top-candidate confidence ≥ this → <c>AutoUse</c>.</summary>
    public double AutoUseConfidence { get; set; } = 0.92;

    /// <summary>Top-candidate confidence in <c>[ConfirmFloor, AutoUseConfidence)</c> → <c>RequireConfirm</c>.</summary>
    public double ConfirmFloor { get; set; } = 0.70;

    /// <summary>
    /// How many top-heuristic candidates to sample-open. Files beyond
    /// this are reported to the ranker with
    /// <see cref="SuavoAgent.Contracts.Discovery.SampleOutcome.NotSampled"/>
    /// so the ranker can still see them at heuristic-only strength.
    /// </summary>
    public int SampleDepth { get; set; } = 5;

    /// <summary>
    /// How many candidates (total, including best) we forward from the
    /// enumerator's scored list to the ranker. Hard upper bound on
    /// ranking cost; the result is further capped by
    /// <c>FileDiscoverySpec.MaxCandidates</c>.
    /// </summary>
    public int MaxCandidatesForRanker { get; set; } = 20;
}
