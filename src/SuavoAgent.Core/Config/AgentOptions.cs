namespace SuavoAgent.Core.Config;

public sealed class AgentOptions
{
    public string CloudUrl { get; set; } = "https://suavollc.com";
    public string? ApiKey { get; set; }
    public string? AgentId { get; set; }
    public string? PharmacyId { get; set; }
    public string? MachineFingerprint { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int HeartbeatJitterSeconds { get; set; } = 5;
    public string Version { get; set; } = "2.0.0";
    public string? SqlServer { get; set; }
    public string? SqlDatabase { get; set; }
    public string? SqlUser { get; set; }
    public string? SqlPassword { get; set; }
}
