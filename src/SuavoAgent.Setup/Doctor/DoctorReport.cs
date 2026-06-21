// src/SuavoAgent.Setup/Doctor/DoctorReport.cs
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

public sealed record DoctorReport(string Version, IReadOnlyList<GateResult> Layers)
{
    public bool HasFailure => Layers.Any(l => l.State == GateState.Fail);

    public static string ToJson(DoctorReport report) => JsonSerializer.Serialize(new
    {
        version = report.Version,
        healthy = !report.HasFailure,
        layers = report.Layers.Select(l => new { name = l.Name, state = l.State.ToString(), detail = l.Detail }),
    }, new JsonSerializerOptions { WriteIndented = true });

    public static string ToTable(DoctorReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SuavoAgent Doctor — {report.Version}  ({(report.HasFailure ? "DEGRADED" : "healthy")})");
        sb.AppendLine("  LAYER             | STATUS | DETAIL");
        sb.AppendLine("  ------------------+--------+--------------------------------------------------");
        foreach (var l in report.Layers)
            sb.AppendLine($"  {l.Name,-17} | {l.State,-6} | {l.Detail}");
        return sb.ToString();
    }
}
