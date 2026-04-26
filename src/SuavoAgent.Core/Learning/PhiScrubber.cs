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

    // Contextual name: 2-word capitalized name near a PHI keyword
    [GeneratedRegex(@"\b(?:Patient|Name|Rx|DOB|SSN|Phone|Address)\s*[:=\-|]\s*([A-Z][a-z]+\s+[A-Z][a-z]+)", RegexOptions.Compiled)]
    private static partial Regex ContextualNamePattern();

    // NPI: 10-digit National Provider Identifier (HIPAA Safe Harbor identifier #6)
    [GeneratedRegex(@"\bNPI[:\s#]?\s*\d{10}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex NpiPattern();

    // DEA number: 2 letters + 7 digits (e.g. AB1234567)
    [GeneratedRegex(@"\b[A-Z]{2}\d{7}\b", RegexOptions.Compiled)]
    private static partial Regex DeaPattern();

    // Rx number in context: "Rx #12345", "RxNo: 12345", "Rx Number 12345"
    [GeneratedRegex(@"\bRx\s*[#Nn]o?[:\s.]+\d{4,10}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex RxNumberPattern();

    // ──────────────────────────────────────────────────────────────────────
    // Codex 2026-04-26 audit gap closures. PMS systems commonly display
    // patient names in formats the original scrubber did not catch:
    //
    //   LastFirstPattern        — "Doe, John" / "Doe, John A" / "Doe, John A."
    //   AllCapsNameContext      — "Patient: DOE, JOHN" / "Name: DOE JOHN"
    //   IcdContextPattern       — diagnosis codes adjacent to keyword
    //
    // Each is anchored on either a punctuation pattern (comma) or a
    // prefix keyword to keep false-positive rates low. UI labels like
    // "Submit" and acronyms like "USA" stay intact.
    // ──────────────────────────────────────────────────────────────────────

    // "Doe, John" or "Doe, John A" or "Doe, John A." — the canonical pharmacy
    // PMS display format. Anchor on the comma to avoid matching incidental
    // capitalized two-word phrases. Replaces capture group 1 (full name).
    [GeneratedRegex(@"\b([A-Z][a-z]+,\s+[A-Z][a-z]+(?:\s+[A-Z]\.?)?)\b", RegexOptions.Compiled)]
    private static partial Regex LastFirstPattern();

    // "Patient: DOE, JOHN", "Name: DOE JOHN A", etc. — uppercase names
    // following a PHI-context keyword. Requires the keyword + colon/equals
    // separator + ≥2 caps tokens. False-positive resistant because random
    // ALLCAPS in the wild rarely follows a keyword separator.
    [GeneratedRegex(@"(?<=\b(?:Patient|Name|DOB|Caregiver)[:\s=\-|]+)([A-Z]{2,}(?:[\s,]+[A-Z]{2,}(?:\s+[A-Z]\.?)?)+)\b", RegexOptions.Compiled)]
    private static partial Regex AllCapsNameContextPattern();

    // ICD-10 diagnosis codes — "E11.9", "F32.1", "Z79.4". HIPAA Safe Harbor
    // identifier #18 (any other unique identifying number, characteristic
    // or code that could re-identify a patient when combined with other
    // data). Require keyword context to avoid matching button labels like
    // "B12" or alphanumeric IDs that happen to fit the ICD shape.
    [GeneratedRegex(@"(?<=\b(?:Dx|Diagnosis|ICD|Code)[:\s=]+)([A-TV-Z][0-9][0-9AB](?:\.[0-9A-TV-Z]{1,4})?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex IcdContextPattern();

    private static readonly Regex[] PhiPatterns =
    [
        SsnPattern(), PhonePattern(), DatePattern(), MrnPattern(),
        NameContextPattern(), LeadingNamePattern(),
        NpiPattern(), DeaPattern(), RxNumberPattern(), ContextualNamePattern(),
        LastFirstPattern(), AllCapsNameContextPattern(), IcdContextPattern(),
    ];

    public static string? ScrubText(string? text)
    {
        if (text is null) return null;

        var result = text;
        foreach (var pattern in PhiPatterns)
        {
            // Patterns that capture the PHI substring inside a structural
            // wrapper (keyword prefix + value) replace only group 1. Plain
            // patterns that match PHI directly replace the whole match.
            if (pattern == ContextualNamePattern() ||
                pattern == LastFirstPattern() ||
                pattern == AllCapsNameContextPattern() ||
                pattern == IcdContextPattern())
            {
                result = pattern.Replace(result, m =>
                {
                    if (m.Groups.Count > 1 && m.Groups[1].Success)
                    {
                        var phi = m.Groups[1].Value;
                        var label = pattern == IcdContextPattern() ? "[CODE]" : "[NAME]";
                        return m.Value.Replace(phi, label);
                    }
                    return Redacted;
                });
            }
            else
            {
                result = pattern.Replace(result, Redacted);
            }
        }
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
