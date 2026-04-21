namespace SuavoAgent.Contracts.Discovery;

/// <summary>
/// Final output of <c>FileLocatorService</c>. Each candidate carries its
/// own <see cref="CandidateRanking"/> with reason + tier + optional
/// per-signal breakdown, so the portal can show "why #1" and compliance
/// audits can trace every decision back to its source tier.
/// </summary>
/// <param name="Best">
/// The top-ranked candidate when any survived. Null when
/// <see cref="Resolution"/> is <see cref="FileDiscoveryResolution.NotFound"/>.
/// </param>
/// <param name="Alternatives">
/// Remaining candidates in rank order (highest confidence first), excluding
/// <see cref="Best"/>. May be empty.
/// </param>
/// <param name="Resolution">
/// Whether <see cref="Best"/> is confident enough to use automatically,
/// needs portal confirmation, is too ambiguous, or nothing was found.
/// </param>
public sealed record FileDiscoveryResult(
    CandidateRanking? Best,
    IReadOnlyList<CandidateRanking> Alternatives,
    FileDiscoveryResolution Resolution);

public enum FileDiscoveryResolution
{
    /// <summary>Confidence ≥ AutoUseConfidence. Use Best without operator check.</summary>
    AutoUse,

    /// <summary>Confidence in confirm-band. Portal shows top candidates, operator picks one.</summary>
    RequireConfirm,

    /// <summary>Confidence below confirm floor. Operator must supply a path or refine hints.</summary>
    Inconclusive,

    /// <summary>No candidates survived enumeration. File probably isn't on this machine.</summary>
    NotFound,
}
