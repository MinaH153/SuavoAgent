using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

public sealed class PhaseGate
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId;
    private readonly string _phase;
    private readonly string? _seedDigest;
    private readonly DateTimeOffset _phaseStartedAt;
    private readonly bool _canaryClean;
    private readonly int _unseededPatternCount;

    private static readonly TimeSpan PatternFloor = TimeSpan.FromHours(72);
    private static readonly TimeSpan ModelFloor = TimeSpan.FromHours(48);
    private const double ConfirmationThreshold = 0.80;
    private const double AbortThreshold = 0.50;
    private const int UnseededMinimum = 5;

    public PhaseGate(AgentStateDb db, string sessionId, string phase, string? seedDigest,
        DateTimeOffset phaseStartedAt, bool canaryClean, int unseededPatternCount)
    {
        _db = db;
        _sessionId = sessionId;
        _phase = phase;
        _seedDigest = seedDigest;
        _phaseStartedAt = phaseStartedAt;
        _canaryClean = canaryClean;
        _unseededPatternCount = unseededPatternCount;
    }

    public record EvaluateResult(bool Ready, IReadOnlyList<GateResult> Gates, bool AbortAcceleration = false);

    public EvaluateResult Evaluate()
    {
        var floor = _phase == "model" ? ModelFloor : PatternFloor;
        var elapsed = DateTimeOffset.UtcNow - _phaseStartedAt;

        var calendarPassed = elapsed >= floor;
        var canaryPassed = _canaryClean;
        var unseededPassed = _unseededPatternCount >= UnseededMinimum;

        double confirmationRatio = 0.0;
        bool confirmationPassed = false;
        bool abort = false;

        if (_seedDigest is not null)
        {
            confirmationRatio = _db.GetSeedConfirmationRatio(_seedDigest);
            confirmationPassed = confirmationRatio >= ConfirmationThreshold;
            abort = confirmationRatio < AbortThreshold && elapsed > TimeSpan.FromHours(24);
        }

        var gates = new List<GateResult>
        {
            new("seeded_confirmation", confirmationPassed, $"{confirmationRatio:P0} confirmed"),
            new("unseeded_minimum", unseededPassed, $"{_unseededPatternCount} unseeded patterns"),
            new("calendar_floor", calendarPassed, $"{elapsed.TotalHours:F0}h elapsed, {floor.TotalHours}h required"),
            new("canary_clean", canaryPassed, canaryPassed ? "clean" : "warning detected"),
        };

        var ready = confirmationPassed && unseededPassed && calendarPassed && canaryPassed;
        return new(ready, gates, abort);
    }
}
