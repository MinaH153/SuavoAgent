using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Intelligence;

public sealed class ContextAssembler
{
    private readonly AgentStateDb _db;

    public ContextAssembler(AgentStateDb db) => _db = db;

    public IntelligenceContext AssembleContext(string businessId, string? sessionId = null)
    {
        return new IntelligenceContext
        {
            BusinessId = businessId,
            AssembledAt = DateTimeOffset.UtcNow,
            Industry = "pharmacy", // TODO: read from business_meta when populated
            StationInfo = new StationInfo
            {
                ProcessorCount = Environment.ProcessorCount,
                MonitorCount = 1,
                OsVersion = Environment.OSVersion.VersionString
            }
        };
    }

    public string? SerializeAndValidate(IntelligenceContext context)
    {
        var json = JsonSerializer.Serialize(context, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var (isClean, _) = ComplianceBoundary.Validate(json);
        return isClean ? json : null;
    }
}

public sealed class IntelligenceContext
{
    public string BusinessId { get; set; } = "";
    public DateTimeOffset AssembledAt { get; set; }
    public string Industry { get; set; } = "unknown";
    public Dictionary<string, int> AppUsageSummary { get; set; } = new();
    public Dictionary<string, int> TemporalSnapshot { get; set; } = new();
    public StationInfo StationInfo { get; set; } = new();
}

public sealed class StationInfo
{
    public int ProcessorCount { get; set; }
    public int MonitorCount { get; set; }
    public string OsVersion { get; set; } = "";
}
