namespace SuavoAgent.Core.Learning;

/// <summary>
/// Fail-closed automatic progression for the observation-only learning phases.
/// Human approval remains the only path out of <c>model</c>.
/// </summary>
internal static class LearningPhaseProgression
{
    internal static readonly TimeSpan DiscoveryDuration = TimeSpan.FromDays(7);
    internal static readonly TimeSpan PatternDuration = TimeSpan.FromDays(14);

    internal sealed record Decision(string NextPhase, string Reason);

    internal static Decision? Evaluate(
        string phase,
        DateTimeOffset phaseStartedAt,
        DateTimeOffset now,
        bool patternPhaseGateReady)
    {
        var elapsed = now >= phaseStartedAt ? now - phaseStartedAt : TimeSpan.Zero;

        return phase switch
        {
            "discovery" when elapsed >= DiscoveryDuration =>
                new Decision("pattern", "discovery_duration"),
            "pattern" when patternPhaseGateReady =>
                new Decision("model", "pattern_phase_gate"),
            "pattern" when elapsed >= PatternDuration =>
                new Decision("model", "pattern_duration"),
            // model -> approved and approved -> active are deliberately absent.
            _ => null,
        };
    }
}
