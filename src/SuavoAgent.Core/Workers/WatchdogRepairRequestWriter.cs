using System.Text.Json;
using SuavoAgent.Core.Health;

namespace SuavoAgent.Core.Workers;

internal static class WatchdogRepairRequestWriter
{
    /// Closed set of repair reasons that may be persisted to the watchdog
    /// request file. Defense-in-depth against PHI ending up in the reason
    /// field (the watchdog redacts as a last line, but we reject up front
    /// so the operator gets clear feedback instead of a silent re-map).
    internal static readonly string[] AllowedReasons =
    [
        "remote_command",
        "watchdog_critical",
        "cloud_stale",
        "install_repair",
        "runtime_health_missing",
        "operator_requested",
    ];

    public enum ReasonValidation
    {
        /// Caller omitted reason — defaulted to "remote_command".
        Defaulted,
        /// Reason matched the allowed set verbatim.
        Accepted,
        /// Reason was provided but isn't in the allowed set. Caller must NACK.
        Rejected,
    }

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

    /// Bug 20 — surface unrecognized reasons to the caller instead of
    /// silently re-mapping to "remote_command". Returns the JSON value
    /// (raw, unsanitized) when present so error messages can echo the
    /// rejected token back to the operator. `null` means the field was
    /// missing or non-string (callers default to "remote_command").
    public static (string? raw, ReasonValidation result) InspectReason(JsonElement dataEl)
    {
        if (!dataEl.TryGetProperty("reason", out var reasonEl) ||
            reasonEl.ValueKind != JsonValueKind.String)
            return (null, ReasonValidation.Defaulted);

        var raw = reasonEl.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return (raw, ReasonValidation.Defaulted);

        var sanitized = SanitizeToken(raw, "");
        return Array.IndexOf(AllowedReasons, sanitized) >= 0
            ? (sanitized, ReasonValidation.Accepted)
            : (raw, ReasonValidation.Rejected);
    }

    private static string NormalizeReason(string? value)
    {
        var reason = SanitizeToken(value, "remote_command");
        return Array.IndexOf(AllowedReasons, reason) >= 0
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
