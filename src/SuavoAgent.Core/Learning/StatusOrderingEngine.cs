using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Infers workflow status ordering and meaning from discovered status values.
/// Maps statuses to delivery-ready indicators using name heuristics.
/// </summary>
public sealed class StatusOrderingEngine
{
    private readonly AgentStateDb _db;

    public StatusOrderingEngine(AgentStateDb db) => _db = db;

    private static readonly (string Pattern, string Meaning, int Order)[] StatusPatterns =
    [
        ("data entry", "queued", 1),
        ("pre-check", "queued", 2),
        ("print", "in_progress", 3),
        ("compound", "in_progress", 4),
        ("fill", "in_progress", 5),
        ("check", "in_progress", 6),
        ("bin", "ready_pickup", 7),
        ("pick up", "ready_pickup", 8),
        ("pickup", "ready_pickup", 8),
        ("out for", "in_transit", 10),
        ("in transit", "in_transit", 10),
        ("delivery", "ready_pickup", 9),
        ("delivered", "delivered", 11),
        ("complete", "completed", 12),
        ("cancel", "cancelled", 13),
        ("void", "cancelled", 14),
        ("return", "returned", 15),
    ];

    public static string InferMeaning(string statusName)
    {
        var lower = statusName.ToLowerInvariant();
        foreach (var (pattern, meaning, _) in StatusPatterns)
        {
            if (lower.Contains(pattern))
                return meaning;
        }
        return "unknown";
    }

    public static int InferOrder(string statusName)
    {
        var lower = statusName.ToLowerInvariant();
        foreach (var (pattern, _, order) in StatusPatterns)
        {
            if (lower.Contains(pattern))
                return order;
        }
        return 99;
    }

    public void InferAndPersist(string sessionId, string schemaTable,
        string statusColumn, IEnumerable<(string Value, string DisplayName)> statuses)
    {
        foreach (var (value, name) in statuses)
        {
            var meaning = InferMeaning(name);
            var order = InferOrder(name);
            var confidence = meaning == "unknown" ? 0.3 : 0.8;

            _db.InsertDiscoveredStatus(sessionId, schemaTable, statusColumn,
                value, meaning, order, 0, confidence);
        }

        _db.AppendLearningAudit(sessionId, "pattern", "status_ordering",
            schemaTable, phiScrubbed: false);
    }

    /// <summary>
    /// Returns status values that indicate delivery-ready prescriptions.
    /// Used by the adapter generator to build the detection query WHERE clause.
    /// </summary>
    public static IReadOnlyList<string> GetDeliveryReadyValues(
        IReadOnlyList<(string StatusValue, string? InferredMeaning, int TransitionOrder, double Confidence)> statuses)
    {
        return statuses
            .Where(s => s.InferredMeaning is "ready_pickup" or "in_transit")
            .Select(s => s.StatusValue)
            .ToList();
    }
}
