using System.Text.RegularExpressions;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Defense-in-depth PHI guard for Phase-5.2 sandbox actuation. Workflows in
/// this phase target Notepad / Calc only, never PioneerRx, so any text we
/// would TYPE that resembles PHI is by definition out of contract — reject
/// before SendInput touches the keyboard buffer.
///
/// Patterns are intentionally narrow at this phase. The PII allowlist on
/// <c>/api/agent/sync</c> (SP1 T1.3) is the canonical PHI gate; this is a
/// second layer that runs in the Helper process, after every transformation.
///
/// Patterns mirror <c>src/lib/phi-scrub.ts</c> on the cloud side, plus the
/// in-agent <c>SuavoAgent.Core.Audit.PhiScrubber</c> additions from
/// p1-production-criticals (memory: PIAG-1 ship complete 2026-04-26).
/// </summary>
public static partial class PhiPatternGuard
{
    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex SsnPattern();

    [GeneratedRegex(@"\b\d{10,11}\b", RegexOptions.CultureInvariant)]
    private static partial Regex NdcOrPhonePattern();

    [GeneratedRegex(@"\b(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"\b(?:\d{1,5}\s+)?[A-Za-z][A-Za-z0-9\s]*\b(?:Street|St|Avenue|Ave|Boulevard|Blvd|Drive|Dr|Lane|Ln|Court|Ct|Place|Pl|Way|Road|Rd)\b\.?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StreetAddressPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b(MRN|DOB|Rx|RxNumber|InsuranceId|MemberId|Policy|Patient|PatientName|NPI|DEA)\s*[:#]?\s*\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StructuredFieldPattern();

    [GeneratedRegex(@"\b[A-Z]{2}\d{7}\b", RegexOptions.CultureInvariant)]
    private static partial Regex DeaPattern();

    public static bool ContainsPotentialPhi(string text, out string? matchedPattern)
    {
        if (string.IsNullOrEmpty(text))
        {
            matchedPattern = null;
            return false;
        }

        if (SsnPattern().IsMatch(text)) { matchedPattern = "ssn"; return true; }
        if (StructuredFieldPattern().IsMatch(text)) { matchedPattern = "structured_field"; return true; }
        if (DeaPattern().IsMatch(text)) { matchedPattern = "dea"; return true; }
        if (StreetAddressPattern().IsMatch(text)) { matchedPattern = "street_address"; return true; }
        if (EmailPattern().IsMatch(text)) { matchedPattern = "email"; return true; }

        // Phone / NDC overlap is acceptable — both are PHI-adjacent enough
        // that we refuse them on the keyboard surface in this phase.
        if (NdcOrPhonePattern().IsMatch(text)) { matchedPattern = "ndc_or_phone"; return true; }
        if (PhonePattern().IsMatch(text)) { matchedPattern = "phone"; return true; }

        matchedPattern = null;
        return false;
    }
}
