namespace SuavoAgent.Events;

/// <summary>
/// Who emitted the event. Per <c>invariants.md §I.3 Authentication + attribution</c>
/// every audit event must be attributable to exactly one actor type.
/// </summary>
public enum ActorType
{
    /// <summary>A fleet operator (human) triggered this event.</summary>
    Operator,

    /// <summary>The on-box agent emitted this event.</summary>
    Agent,

    /// <summary>Cloud dispatcher (Phase C+) emitted this event.</summary>
    CloudDispatcher,

    /// <summary>System process (cron, scheduled job, trigger).</summary>
    System
}
