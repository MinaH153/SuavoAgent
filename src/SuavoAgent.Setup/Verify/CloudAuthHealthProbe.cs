// src/SuavoAgent.Setup/Verify/CloudAuthHealthProbe.cs
using System;
using System.IO;
using System.Text.Json;

namespace SuavoAgent.Setup.Verify;

/// <summary>Reads cloud-auth-health.json: status "ok" passes; a 401 / agent_not_found error kind fails.</summary>
public sealed class CloudAuthHealthProbe
{
    private readonly Func<string?> _readHealthJson;

    public CloudAuthHealthProbe(Func<string?>? readHealthJson = null)
        => _readHealthJson = readHealthJson ?? ReadDefault;

    public GateResult Check()
    {
        var json = _readHealthJson();
        if (string.IsNullOrWhiteSpace(json))
            return new GateResult("Cloud auth", GateState.Warn, "Cloud auth status not yet written");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var errKind = root.TryGetProperty("lastErrorKind", out var e) && e.ValueKind == JsonValueKind.String
                ? e.GetString() : null;
            if (!string.IsNullOrEmpty(errKind) &&
                (errKind.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                 errKind.Contains("agent_not_found", StringComparison.OrdinalIgnoreCase)))
                return new GateResult("Cloud auth", GateState.Fail, $"Cloud auth failing: {errKind}");
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                return new GateResult("Cloud auth", GateState.Ok, "Cloud auth healthy");
            return new GateResult("Cloud auth", GateState.Warn, $"Cloud auth status: {status ?? "unknown"}");
        }
        catch
        {
            return new GateResult("Cloud auth", GateState.Warn, "Cloud auth status unreadable");
        }
    }

    private static string? ReadDefault()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "cloud-auth-health.json");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
