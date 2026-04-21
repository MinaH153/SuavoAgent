namespace SuavoAgent.Events;

/// <summary>
/// Event severity per docs/self-healing/event-registry.md. Matches the CHECK
/// constraint on the cloud <c>audit_events.severity</c> column.
/// </summary>
public enum EventSeverity
{
    Info,
    Warn,
    Error,
    Critical
}
