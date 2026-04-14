// src/SuavoAgent.Core/Learning/PhiScrubber.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// In-memory PHI detection and scrubbing. Runs before any observation is persisted.
/// Implements observe-hash-discard pattern per HIPAA Safe Harbor (45 CFR 164.514).
/// </summary>
public static partial class PhiScrubber
{
    private const string Redacted = "[REDACTED]";

    // Phone: (555) 123-4567, 555-123-4567, 5551234567
    [GeneratedRegex(@"\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}", RegexOptions.Compiled)]
    private static partial Regex PhonePattern();

    // SSN: 123-45-6789
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled)]
    private static partial Regex SsnPattern();

    // Date: 01/15/1990, 1990-01-15, 01-15-1990
    [GeneratedRegex(@"\b\d{1,2}[/-]\d{1,2}[/-]\d{2,4}\b|\b\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled)]
    private static partial Regex DatePattern();

    // MRN: MRN: ABC12345, MRN:12345 — preserve prefix, scrub value
    [GeneratedRegex(@"(?<=MRN[:\s]+)\w+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex MrnPattern();

    // Name heuristic: "Patient: First Last", "DOB: 01/15/1990" — preserve keyword, scrub value
    [GeneratedRegex(@"(?<=(?:Patient|Name|DOB|SSN)[:\s]+)\S+(?:\s+\S+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex NameContextPattern();

    // Capitalized name pair at start of string: "John Smith - ..."
    [GeneratedRegex(@"^[A-Z][a-z]+\s+[A-Z][a-z]+(?=\s+-)", RegexOptions.Compiled)]
    private static partial Regex LeadingNamePattern();

    // Contextual name: 2-word capitalized name near a PHI keyword (Patient, Name, Rx, DOB, SSN, Phone, Address)
    // Word boundary prevents matching inside compound words like "PioneerRx"
    [GeneratedRegex(@"\b(?:Patient|Name|Rx|DOB|SSN|Phone|Address)\s*[:=\-|]\s*([A-Z][a-z]+\s+[A-Z][a-z]+)", RegexOptions.Compiled)]
    private static partial Regex ContextualNamePattern();

    private static readonly Regex[] PhiPatterns = new[]
    {
        SsnPattern(), PhonePattern(), DatePattern(), MrnPattern(),
        NameContextPattern(), LeadingNamePattern()
    };

    public static string? ScrubText(string? text)
    {
        if (text is null) return null;

        var result = text;
        foreach (var pattern in PhiPatterns)
            result = pattern.Replace(result, Redacted);

        // Apply contextual name pattern last — replaces capture group 1 (the name) with [NAME]
        result = ContextualNamePattern().Replace(result, m =>
            m.Value.Replace(m.Groups[1].Value, "[NAME]"));
        return result;
    }

    public static bool ContainsPhi(string text)
    {
        foreach (var pattern in PhiPatterns)
            if (pattern.IsMatch(text)) return true;
        return false;
    }

    /// <summary>
    /// HMAC-SHA256 hash with per-pharmacy salt. Not dictionary-attackable like plain SHA-256.
    /// </summary>
    public static string HmacHash(string value, string salt)
    {
        var key = Encoding.UTF8.GetBytes(salt);
        var data = Encoding.UTF8.GetBytes(value);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
