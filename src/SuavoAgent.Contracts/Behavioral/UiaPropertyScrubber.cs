using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Contracts.Behavioral;

/// <summary>
/// Raw UIA element properties before HIPAA scrubbing.
/// Name may contain PHI — must be hashed before leaving this layer.
/// </summary>
public sealed record RawElementProperties(
    string? ControlType,
    string? AutomationId,
    string? ClassName,
    string? Name,
    string? BoundingRect,
    int Depth,
    int ChildIndex);

/// <summary>
/// HIPAA boundary enforcer for UIA element properties.
///
/// Tiers:
///   GREEN  — bounded structural metadata after vendor-controlled identifier validation
///   YELLOW — column headers, always HMAC-hashed
///   RED    — Value, Text, Selection, HelpText, ItemStatus — NEVER collected
/// </summary>
public static class UiaPropertyScrubber
{
    /// <summary>
    /// Scrubs all properties. Name is HMAC-hashed; nulls allowed.
    /// </summary>
    public static ScrubbedElement Scrub(RawElementProperties raw, string pharmacySalt) =>
        new(
            ControlType: raw.ControlType,
            AutomationId: StructuralIdentifierSanitizer.AllowOrNull(raw.AutomationId),
            ClassName: StructuralIdentifierSanitizer.AllowOrNull(raw.ClassName),
            NameHash: raw.Name is null ? null : HmacHash(raw.Name, pharmacySalt),
            BoundingRect: raw.BoundingRect,
            Depth: raw.Depth,
            ChildIndex: raw.ChildIndex);

    /// <summary>
    /// Returns null if element is anonymous (no AutomationId AND no ClassName) — unidentifiable across sessions.
    /// </summary>
    public static ScrubbedElement? TryScrub(RawElementProperties raw, string pharmacySalt)
    {
        if (!StructuralIdentifierSanitizer.IsAllowed(raw.AutomationId) &&
            !StructuralIdentifierSanitizer.IsAllowed(raw.ClassName))
            return null;

        return Scrub(raw, pharmacySalt);
    }

    /// <summary>
    /// Prefers AutomationId. Falls back to ClassName:Depth:ChildIndex. Returns null if neither exists.
    /// </summary>
    public static string? BuildElementId(RawElementProperties raw)
    {
        if (StructuralIdentifierSanitizer.IsAllowed(raw.AutomationId))
            return raw.AutomationId;

        if (StructuralIdentifierSanitizer.IsAllowed(raw.ClassName))
            return $"{raw.ClassName}:{raw.Depth}:{raw.ChildIndex}";

        return null;
    }

    /// <summary>
    /// YELLOW tier: column headers are always HMAC-hashed (even though they're not PHI,
    /// they reveal PMS schema structure).
    /// </summary>
    public static string ScrubColumnHeader(string header, string pharmacySalt) =>
        HmacHash(header, pharmacySalt);

    public static string HmacHash(string value, string salt)
    {
        var keyBytes = Encoding.UTF8.GetBytes(salt);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var hash = HMACSHA256.HashData(keyBytes, valueBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
