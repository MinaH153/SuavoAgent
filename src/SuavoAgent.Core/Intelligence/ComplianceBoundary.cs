using System.Text.RegularExpressions;

namespace SuavoAgent.Core.Intelligence;

public static class ComplianceBoundary
{
    private static readonly Regex[] PhiPatterns =
    {
        new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled),             // SSN
        new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled), // Email
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
        if (Regex.IsMatch(value, @"^\d+\.\d+\.\d+$")) return true; // version numbers
        if (value.Contains('T') && value.Contains(':')) return true; // ISO timestamps
        return false;
    }

    private static string Redact(string value) =>
        value.Length <= 4 ? "****" : value[..2] + "****" + value[^2..];
}
