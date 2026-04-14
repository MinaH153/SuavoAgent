namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Directive types that the feedback loop can emit to adjust behavioral learning.
/// </summary>
public enum DirectiveType
{
    ConfidenceAdjust,
    Promote,
    Demote,
    Prune,
    Recalibrate,
    ReLearn,
    ThresholdAdjust,
    SuspendPromotion,
    EscalateStale
}

/// <summary>
/// Immutable event recording a feedback signal and the directive it produced.
/// Foundation type for Spec C: Self-Improving Feedback System.
/// </summary>
public sealed record FeedbackEvent(
    string SessionId,
    string EventType,
    string Source,
    string? SourceId,
    string TargetType,
    string TargetId,
    string? PayloadJson,
    DirectiveType DirectiveType,
    string? DirectiveJson,
    string? CausalChainJson)
{
    // --- Init-only metadata ---
    public int? Id { get; init; }
    public string? AppliedAt { get; init; }
    public string? AppliedBy { get; init; }
    public string CreatedAt { get; init; } = DateTimeOffset.UtcNow.ToString("o");

    // --- Constants ---
    public const double ConfidenceCeiling = 0.95;
    public const double ConfidenceFloor = 0.1;
    public const double DecayFloor = 0.5;
    public const double DecayAmount = 0.01;
    public const int PromotionSuspendThreshold = 5;
    public const int RecalibrationMinSamples = 20;
    public const int StaleTtlDays = 14;
    public const int DecayIdleDays = 7;

    // --- Outcome-to-delta mapping ---
    private static readonly Dictionary<string, double> OutcomeDeltas = new()
    {
        ["success"] = 0.05,
        ["already_at_target"] = 0.02,
        ["verified_with_drift"] = 0.03,
        ["post_verify_mismatch"] = -0.10,
        ["status_conflict"] = -0.15,
        ["sql_error"] = -0.05,
        ["connection_reset"] = 0.0,
        ["trigger_blocked"] = -0.08
    };

    /// <summary>
    /// Maps a writeback/verification outcome string to its confidence delta.
    /// Unknown outcomes return 0.0 (no change).
    /// </summary>
    public static double OutcomeToDelta(string outcome)
        => OutcomeDeltas.TryGetValue(outcome, out var delta) ? delta : 0.0;

    /// <summary>
    /// Applies a confidence delta, clamping to [ConfidenceFloor, ConfidenceCeiling].
    /// Uses Math.Round(x, 10) to prevent floating-point drift.
    /// </summary>
    public static double ApplyConfidenceDelta(double current, double delta)
        => Math.Round(Math.Clamp(current + delta, ConfidenceFloor, ConfidenceCeiling), 10);

    /// <summary>
    /// Applies idle decay: subtracts DecayAmount, floored at DecayFloor.
    /// Returns DecayFloor if already at or below it.
    /// </summary>
    public static double ApplyDecay(double current)
        => current <= DecayFloor
            ? DecayFloor
            : Math.Round(Math.Max(DecayFloor, current - DecayAmount), 10);
}
