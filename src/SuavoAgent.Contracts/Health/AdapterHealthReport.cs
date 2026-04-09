namespace SuavoAgent.Contracts.Health;

public record AdapterHealthReport(
    string AdapterName,
    bool IsHealthy,
    string? SqlStatus,
    string? UiaStatus,
    string? ApiStatus,
    DateTimeOffset CheckedAt,
    IReadOnlyDictionary<string, string>? Details);
