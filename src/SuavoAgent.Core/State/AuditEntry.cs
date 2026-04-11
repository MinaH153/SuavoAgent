namespace SuavoAgent.Core.State;

public record AuditEntry(
    string TaskId,
    string EventType,
    string FromState,
    string ToState,
    string Trigger,
    string? CommandId = null,
    string? RequesterId = null,
    string? RxNumber = null);
