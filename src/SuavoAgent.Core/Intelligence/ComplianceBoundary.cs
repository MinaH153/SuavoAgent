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

    private static readonly HashSet<string> MustBeHashedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowTitleHash", "nameHash", "machineNameHash", "docHash",
        "schemaFingerprint", "userSidHash", "treeHash"
    };

    public static (bool IsClean, List<string> Violations) Validate(string json)
    {
        var violations = new List<string>();
        foreach (var pattern in PhiPatterns)
        {
            var matches = pattern.Matches(json);
            foreach (Match match in matches)
            {
                if (!IsSafeValue(match.Value))
                    violations.Add($"PHI pattern detected: '{Redact(match.Value)}'");
            }
        }
        return (violations.Count == 0, violations);
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
        return false;
    }

    private static string Redact(string value) =>
        value.Length <= 4 ? "****" : value[..2] + "****" + value[^2..];
}
