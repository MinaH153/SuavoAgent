using System.Text.RegularExpressions;

namespace SuavoAgent.Core.Intelligence;

public static class ComplianceBoundary
{
    private static readonly Regex[] PhiPatterns =
    {
        new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled),                           // SSN
        new(@"\b\d{3}[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled),               // Phone (10-digit)
        new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled),  // Email
        new(@"\b\d{1,2}/\d{1,2}/\d{2,4}\b", RegexOptions.Compiled),                     // Date (MM/DD/YY or YYYY)
        new(@"\b[A-Z][a-z]{1,15}\s[A-Z][a-z]{1,20}\b", RegexOptions.Compiled),          // Name pair (First Last)
        new(@"\b\d{5}(-\d{4})?\b", RegexOptions.Compiled),                              // ZIP code
        new(@"\bMRN[:\s#]?\d+\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),      // Medical Record Number
        new(@"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", RegexOptions.Compiled),          // IP address (HIPAA identifier #15)
    };

    private static readonly HashSet<string> SafeContextKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "drugName", "itemName", "medicationDisplay", "ndc", "tradeName",
        "industry", "metric", "appName", "processName", "category",
        "version", "agentVersion", "osVersion", "dayOfWeek", "hourKey",
        "loadLevel", "periodType", "periodKey", "fileType",
        // Operational numeric fields — bare numbers that happen to be 5
        // digits must not be mistaken for ZIP codes. Keep in sync with
        // ~/Code/Suavo/src/lib/phi-redaction.ts SAFE_CONTEXT_KEYS.
        "uptime_ms", "memory_mb", "cpu_pct", "duration_ms", "ingest_latency_ms",
        "process_id", "pid", "exit_code", "consecutive_failures", "sequence",
        "verify_duration_ms", "file_count", "event_count", "accepted_count",
        "rejected_count", "escalation_tier", "silent_duration_seconds",
        "max_execution_time_ms", "row_count", "bytes", "size_bytes",
        "columns_observed", "tasks_completed", "tasks_failed",
        "rx_ready_count", "rx_total_synced"
    };

    private static readonly HashSet<string> MustBeHashedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowTitleHash", "nameHash", "machineNameHash", "docHash",
        "schemaFingerprint", "userSidHash", "treeHash"
    };

    private static readonly Regex IsoTimestampValue = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:?\d{2})?$",
        RegexOptions.Compiled);

    public static (bool IsClean, List<string> Violations) Validate(string json)
    {
        var violations = new List<string>();
        foreach (var pattern in PhiPatterns)
        {
            var matches = pattern.Matches(json);
            foreach (Match match in matches)
            {
                if (IsSafeValue(match.Value)) continue;
                if (IsInsideIsoTimestampValue(json, match.Index)) continue;
                // Only exempt if match is a direct JSON value of a safe field name —
                // require "safeKey": immediately before the value. Bare substring match
                // within 40 chars was too broad (adjacent-field false-negative).
                var start = Math.Max(0, match.Index - 60);
                var context = json[start..match.Index];
                if (SafeContextKeywords.Any(k =>
                    context.Contains($"\"{k}\":", StringComparison.OrdinalIgnoreCase) ||
                    context.Contains($"\"{k}\" :", StringComparison.OrdinalIgnoreCase)))
                    continue;
                violations.Add($"PHI pattern detected: '{Redact(match.Value)}'");
            }
        }
        return (violations.Count == 0, violations);
    }

    private static bool IsInsideIsoTimestampValue(string json, int matchIndex)
    {
        var openQuote = json.LastIndexOf('"', matchIndex);
        if (openQuote < 0) return false;
        var closeQuote = json.IndexOf('"', matchIndex);
        if (closeQuote < 0 || closeQuote <= openQuote) return false;
        var value = json[(openQuote + 1)..closeQuote];
        return IsoTimestampValue.IsMatch(value);
    }

    public static (bool IsClean, List<string> Violations) ValidateFields(
        IDictionary<string, object?> fields)
    {
        var violations = new List<string>();
        foreach (var (key, value) in fields)
        {
            if (value == null) continue;
            var strVal = value.ToString() ?? "";
            if (MustBeHashedFields.Contains(key) && strVal.Length > 0 && strVal.Length < 32
                && !strVal.All(c => "0123456789abcdef".Contains(c)))
                violations.Add($"Field '{key}' appears unhashed: '{Redact(strVal)}'");
        }
        return (violations.Count == 0, violations);
    }

    private static bool IsSafeValue(string value)
    {
        // Version numbers (1.2.3) — but NOT IP addresses (each segment must be <256-like semver)
        if (Regex.IsMatch(value, @"^\d{1,3}\.\d{1,3}\.\d{1,3}$"))
        {
            var parts = value.Split('.');
            // IP addresses have segments 0-255; versions typically have at least one segment > 255 or are clearly semver
            // Simple heuristic: if all 3 segments are <= 255, it could be an IP — don't whitelist
            if (parts.All(p => int.TryParse(p, out var n) && n <= 255))
                return false;
            return true;
        }
        // ISO timestamps are safe
        if (value.Contains('T') && value.Contains(':')) return true;
        // NDC patterns (5-4-2 or 5-3-2 digit format)
        if (Regex.IsMatch(value, @"^\d{4,5}-\d{3,4}-\d{1,2}$")) return true;
        return false;
    }

    private static string Redact(string value) =>
        value.Length <= 4 ? "****" : value[..2] + "****" + value[^2..];
}
