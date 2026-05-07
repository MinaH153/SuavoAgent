using System.Text.Json;
using SuavoAgent.Core.Health;

namespace SuavoAgent.Core.Workers;

internal static class WatchdogRepairRequestWriter
{
    public static string Queue(
        string? configuredPath,
        string? commandId,
        string reason,
        string? agentId)
    {
        var requestPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(RuntimeHealthEvidence.ProgramDataRoot, "watchdog-repair-request.json")
            : configuredPath;

        var directory = Path.GetDirectoryName(requestPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var payload = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["commandId"] = SanitizeToken(commandId, "unknown"),
            ["reason"] = NormalizeReason(reason),
            ["requestedAt"] = DateTimeOffset.UtcNow.ToString("o"),
            ["agentId"] = agentId ?? "",
            ["source"] = "signed_remote_repair",
        };

        var json = JsonSerializer.Serialize(payload);
        var tmp = $"{requestPath}.tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(requestPath))
            File.Replace(tmp, requestPath, null);
        else
            File.Move(tmp, requestPath);

        return requestPath;
    }

    public static string ReadReason(JsonElement dataEl)
    {
        if (!dataEl.TryGetProperty("reason", out var reasonEl) ||
            reasonEl.ValueKind != JsonValueKind.String)
            return "remote_command";

        return NormalizeReason(reasonEl.GetString());
    }

    private static string NormalizeReason(string? value)
    {
        var reason = SanitizeToken(value, "remote_command");
        return reason is
            "remote_command" or
            "watchdog_critical" or
            "cloud_stale" or
            "install_repair" or
            "runtime_health_missing" or
            "operator_requested"
                ? reason
                : "remote_command";
    }

    private static string SanitizeToken(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var chars = value
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.')
            .Take(80)
            .ToArray();

        return chars.Length == 0 ? fallback : new string(chars);
    }
}
