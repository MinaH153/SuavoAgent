namespace SuavoAgent.Contracts.Models;

public record CapabilityManifest(
    bool CanReadSql,
    bool CanReadApi,
    bool CanWritebackApi,
    bool CanWritebackUia,
    bool CanReceiveEvents,
    string? PmsVersion,
    string? SqlServerEndpoint,
    string? ApiEndpoint,
    IReadOnlyList<string> DiscoveredScreens);
