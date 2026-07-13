using System.Text.RegularExpressions;

namespace SuavoAgent.Contracts.Behavioral;

/// <summary>
/// Shared fail-closed boundary for vendor-controlled UIA identifiers. A field
/// being named AutomationId or ClassName is not proof that its value is static;
/// dynamic patient/Rx identifiers must never enter learning storage or cloud.
/// </summary>
public static partial class StructuralIdentifierSanitizer
{
    private const int MaximumLength = 256;

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.:$#@-]{0,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex Grammar();

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"(?:^|[^A-Fa-f0-9])[A-Fa-f0-9]{8}-[A-Fa-f0-9]{4}-[1-5A-Fa-f0-9][A-Fa-f0-9]{3}-[89ABA-Fa-f0-9][A-Fa-f0-9]{3}-[A-Fa-f0-9]{12}(?:$|[^A-Fa-f0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex GuidValue();

    [GeneratedRegex(@"\d{7,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongDigitRun();

    [GeneratedRegex(@"(?:^|[_.:$#@-])(?:patient|customer|member|rx|prescription|dob|ssn|phone|address)[_.:$#@-][A-Za-z0-9_.:$#@-]*\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveDynamicIdentifier();

    [GeneratedRegex(@"^anon:path:(?:unavailable|\d+(?:\.\d+){0,15}):rid:(?:unavailable|[a-f0-9]{64}):shape:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex AnonymousElementIdentity();

    public static bool IsAllowed(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !Grammar().IsMatch(value))
            return false;

        return !Email().IsMatch(value) &&
               !GuidValue().IsMatch(value) &&
               !LongDigitRun().IsMatch(value) &&
               !SensitiveDynamicIdentifier().IsMatch(value);
    }

    public static string? AllowOrNull(string? value) => IsAllowed(value) ? value : null;

    /// <summary>
    /// Helper-generated anonymous identities have a separate exact grammar.
    /// Their digests are not vendor text, so digit-run PHI heuristics must not
    /// reject otherwise valid SHA-256 output.
    /// </summary>
    public static bool IsAllowedElementIdentity(string? value) =>
        !string.IsNullOrEmpty(value) &&
        (AnonymousElementIdentity().IsMatch(value) || IsAllowed(value));
}
