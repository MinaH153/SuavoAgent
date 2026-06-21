// src/SuavoAgent.Setup/Doctor/ConfigDoctorProbe.cs
using System;
using System.IO;
using System.Text.Json;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Doctor;

/// <summary>Reads the plain config-overrides.json: reports effective pricing modality + flags a disabled
/// PHI pipe security gate. Never reads DPAPI-sealed secrets.</summary>
public sealed class ConfigDoctorProbe
{
    private readonly Func<string?> _readConfigOverridesJson;

    public ConfigDoctorProbe(Func<string?>? readConfigOverridesJson = null)
        => _readConfigOverridesJson = readConfigOverridesJson ?? ReadDefault;

    public GateResult Check()
    {
        var json = _readConfigOverridesJson();
        var pricing = "UiaFirst";
        var sqlTrust = true;
        var relax = false;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                pricing = ReadString(doc.RootElement, "PricingExecutor") ?? pricing;
                sqlTrust = ReadBool(doc.RootElement, "SqlTrustServerCertificate") ?? sqlTrust;
                relax = ReadBool(doc.RootElement, "RelaxIpcClientPathValidation") ?? relax;
            }
            catch { /* unreadable → report defaults */ }
        }
        var detail = $"PricingExecutor={pricing}, SqlTrustServerCertificate={sqlTrust}";
        return relax
            ? new GateResult("Config", GateState.Fail,
                $"RelaxIpcClientPathValidation is ON — disables the PHI pipe security gate. ({detail})")
            : new GateResult("Config", GateState.Ok, detail);
    }

    // Accepts both flat ("Agent.X") and nested ({"Agent":{"X":...}}) shapes.
    private static string? ReadString(JsonElement root, string key)
    {
        if (root.TryGetProperty($"Agent.{key}", out var flat) && flat.ValueKind == JsonValueKind.String)
            return flat.GetString();
        if (root.TryGetProperty("Agent", out var agent) && agent.ValueKind == JsonValueKind.Object
            && agent.TryGetProperty(key, out var nested) && nested.ValueKind == JsonValueKind.String)
            return nested.GetString();
        return null;
    }

    private static bool? ReadBool(JsonElement root, string key)
    {
        if (root.TryGetProperty($"Agent.{key}", out var flat)
            && (flat.ValueKind == JsonValueKind.True || flat.ValueKind == JsonValueKind.False))
            return flat.GetBoolean();
        if (root.TryGetProperty("Agent", out var agent) && agent.ValueKind == JsonValueKind.Object
            && agent.TryGetProperty(key, out var nested)
            && (nested.ValueKind == JsonValueKind.True || nested.ValueKind == JsonValueKind.False))
            return nested.GetBoolean();
        return null;
    }

    private static string? ReadDefault()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "config-overrides.json");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
}
