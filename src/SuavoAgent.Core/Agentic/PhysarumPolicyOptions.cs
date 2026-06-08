namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Tuning for the <see cref="PhysarumActionPolicy"/> frontier explorer. Defaults are starting guesses to
/// calibrate on real sandbox telemetry. The brain (Qwen3) is consulted only every
/// <see cref="BrainTickInterval"/> steps (oscillatory scheduling) so it never runs every tick — that is
/// the CPU protection for the low-spec box / PioneerRx coexistence.
/// </summary>
public sealed record PhysarumPolicyOptions
{
    /// <summary>Call the inner brain every N steps (step 0 is always a brain tick — start grounded).
    /// Off-ticks select purely from conductance + explore, with zero LLM cost.</summary>
    public int BrainTickInterval { get; init; } = 4;

    /// <summary>Additive bonus a candidate gets when the brain endorsed it this tick (the LLM as a
    /// scorer/interpreter, not the driver — its vote nudges, never vetoes). Scaled to the conductance range.</summary>
    public double BrainEndorsementBonus { get; init; } = 0.15;

    /// <summary>Subtracted per prior visit of the same (state→action) edge in working-memory history —
    /// discourages re-treading and pushes the explorer outward (complements the loop's no-progress cap).</summary>
    public double TrailPenalty { get; init; } = 0.2;

    /// <summary>Upper bound on a small deterministic per-(step,candidate) jitter that breaks ties so a
    /// flat-conductance screen explores different moves on different steps. Kept ≪ the conductance range
    /// so learned edges still dominate. Deterministic (no RNG) → resume-safe + unit-testable.</summary>
    public double ExploreEpsilon { get; init; } = 0.05;

    /// <summary>Max candidates enumerated per step (bounds O(elements) cost on a low-spec box).</summary>
    public int TopK { get; init; } = CandidateEnumerator.DefaultMaxCandidates;

    /// <summary>The allowlisted sandbox process the explorer enumerates click candidates against
    /// (e.g. "notepad" / "calc"). Required for explore runs; the candidates carry it as their actuation target.</summary>
    public string ProcessName { get; init; } = "";
}
