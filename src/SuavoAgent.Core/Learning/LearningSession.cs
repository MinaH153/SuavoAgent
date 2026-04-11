namespace SuavoAgent.Core.Learning;

/// <summary>
/// Manages learning phase transitions and mode promotions.
/// Phase: discovery → pattern → model → approved → active
/// Mode: observer → supervised → autonomous
/// </summary>
public sealed class LearningSession
{
    private static readonly string[] PhaseOrder = { "discovery", "pattern", "model", "approved", "active" };
    private static readonly string[] ModeOrder = { "observer", "supervised", "autonomous" };

    private static readonly Dictionary<string, TimeSpan> PhaseDurations = new()
    {
        ["discovery"] = TimeSpan.FromDays(7),
        ["pattern"] = TimeSpan.FromDays(14),
        ["model"] = TimeSpan.FromDays(9),
    };

    public static bool IsValidPhaseTransition(string from, string to)
    {
        var fromIdx = Array.IndexOf(PhaseOrder, from);
        var toIdx = Array.IndexOf(PhaseOrder, to);
        return fromIdx >= 0 && toIdx == fromIdx + 1;
    }

    public static bool IsValidModeTransition(string from, string to)
    {
        // Forward: observer → supervised → autonomous
        // Backward: any → supervised (downgrade), any → observer (reset)
        if (to == "observer") return true; // full reset always allowed
        if (to == "supervised" && from == "autonomous") return true; // downgrade
        var fromIdx = Array.IndexOf(ModeOrder, from);
        var toIdx = Array.IndexOf(ModeOrder, to);
        return fromIdx >= 0 && toIdx == fromIdx + 1;
    }

    public static ObserverPhase PhaseToObserverPhase(string phase) => phase switch
    {
        "discovery" => ObserverPhase.Discovery,
        "pattern" => ObserverPhase.Pattern,
        "model" => ObserverPhase.Model,
        "active" => ObserverPhase.Active,
        _ => ObserverPhase.Discovery
    };

    public static string? GetNextPhase(string current, DateTimeOffset phaseStarted)
    {
        if (!PhaseDurations.TryGetValue(current, out var duration)) return null;
        if (DateTimeOffset.UtcNow - phaseStarted < duration) return null;
        var idx = Array.IndexOf(PhaseOrder, current);
        return idx >= 0 && idx + 1 < PhaseOrder.Length ? PhaseOrder[idx + 1] : null;
    }
}
