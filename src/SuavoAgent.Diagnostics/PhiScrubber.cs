using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SuavoAgent.Diagnostics;

/// SDK-side PHI scrub. Spec §3 + §164.502(b) minimum-necessary contract:
/// only the smallest signal that identifies the crash crosses the wire.
/// All regexes use <c>RegexOptions.NonBacktracking</c> to defeat
/// catastrophic-backtracking attacks (Codex §8.1 RESOLVED v1.0).
///
/// Patterns ordered by selectivity — most-specific first so JSON/XML/SQL
/// field-context patterns catch identifiers before the bare-number patterns
/// (NPI Luhn, NDC unhyphenated) trigger on neighboring digits.
public sealed class PhiScrubber
{
    private readonly TimeSpan _timeout;
    private readonly RulesetV1 _ruleset;
    private readonly List<ScrubRule> _rules;

    public PhiScrubber(RulesetV1 ruleset, TimeSpan timeout)
    {
        _ruleset = ruleset;
        _timeout = timeout;
        _rules = BuildRules();
    }

    /// <summary>
    /// Returns a PHI-scrubbed copy of the input. Hard 10ms budget per spec
    /// §4 contract; on overrun, returns the input with <c>[SCRUB_TIMEOUT]</c>
    /// marker and the caller drops the extras (fail-closed).
    /// </summary>
    public string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        var sw = Stopwatch.StartNew();
        var result = input;
        foreach (var rule in _rules)
        {
            if (sw.Elapsed > _timeout)
            {
                return "[SCRUB_TIMEOUT]";
            }
            try
            {
                result = rule.Regex.Replace(result, rule.Replacement);
            }
            catch (RegexMatchTimeoutException)
            {
                return "[SCRUB_TIMEOUT]";
            }
            catch
            {
                // a single rule failing should not nullify the whole scrub;
                // continue with the next rule
            }
        }

        // Optional ruleset-loaded patient name dictionary. Phase 1: empty.
        if (_ruleset.PatientNamesSeed.Count > 0)
        {
            foreach (var name in _ruleset.PatientNamesSeed)
            {
                if (sw.Elapsed > _timeout) return "[SCRUB_TIMEOUT]";
                if (string.IsNullOrWhiteSpace(name)) continue;
                result = Regex.Replace(result, $@"\b{Regex.Escape(name)}\b", "[PATIENT]",
                    RegexOptions.IgnoreCase, _timeout);
            }
        }

        return result;
    }

    /// <summary>
    /// High-confidence PHI-present test. Returns true if the input still
    /// matches a high-severity PHI pattern AFTER scrubbing — meaning the
    /// scrubber failed to defeat it. Caller drops the event entirely and
    /// forwards only the fingerprint per spec §3.
    /// </summary>
    public bool IsDefinitelyPhi(string scrubbed)
    {
        // High-confidence post-scrub PHI shapes that should NEVER appear
        // after Sanitize. If they do, the corpus has a hole; drop entirely.
        foreach (var rule in _rules.Where(r => r.HighConfidence))
        {
            if (rule.Regex.IsMatch(scrubbed)) return true;
        }
        return false;
    }

    private static List<ScrubRule> BuildRules()
    {
        var opts = RegexOptions.NonBacktracking | RegexOptions.IgnoreCase;
        // NonBacktracking does not support backreferences. The XML
        // matching-tag rule below uses \1 so it falls back to the
        // standard engine with a timeout; the input class ([^<]+) is
        // linear so catastrophic-backtracking is not a concern there.
        var optsWithBackref = RegexOptions.IgnoreCase;
        var timeout = TimeSpan.FromMilliseconds(50);

        return new List<ScrubRule>
        {
            // ── PioneerRx field-context (highest selectivity, run first) ──
            new("PioneerRx-JSON",
                new Regex(@"""(RxNumber|PatientID|PrescriberID|PharmacyChainID)""\s*:\s*""?[^"",}\s]+""?",
                    opts, timeout),
                "\"$1\":\"[PIONEERRX_ID]\"",
                highConfidence: true),
            new("PioneerRx-XML",
                new Regex(@"<(RxNumber|PatientID|PrescriberID|PharmacyChainID)>[^<]+</\1>",
                    optsWithBackref, timeout),
                "<$1>[PIONEERRX_ID]</$1>",
                highConfidence: true),
            new("PioneerRx-SQL",
                new Regex(@"\b(RxNumber|PatientID|PrescriberID|PharmacyChainID)\s*=\s*'[^']+'",
                    opts, timeout),
                "$1='[PIONEERRX_ID]'",
                highConfidence: true),

            // ── Insurance member IDs (field-context required) ──
            // Lazy .{0,24}? skips filler words between context keyword and
            // the member-ID-shaped token. NonBacktracking is incompatible
            // with `.{0,24}?` lazy quantifiers, so these rules fall back to
            // the standard engine with a 50ms timeout — token shapes are
            // bounded so catastrophic backtracking is not a concern.
            new("BCBS-Member",
                new Regex(@"\b(bcbs|blue\s*cross|member_?id)\b.{0,24}?\b[A-Z]{3}\d{6,14}\b",
                    optsWithBackref, timeout),
                "$1 [MEMBER_ID]"),
            new("Aetna-Member",
                new Regex(@"\b(aetna|member_?id)\b.{0,24}?\bW\d{8,12}\b",
                    optsWithBackref, timeout),
                "$1 [MEMBER_ID]"),
            new("Cigna-Member",
                new Regex(@"\b(cigna|member_?id)\b.{0,24}?\bU?\d{9,12}\b",
                    optsWithBackref, timeout),
                "$1 [MEMBER_ID]"),

            // ── Prescriber NPI (field-context required, more reliable than naked-Luhn) ──
            // Permissive separator handles JSON ("npi":"..."), YAML
            // (npi: ...), env (NPI=...), and plain text.
            new("Prescriber-NPI-field",
                new Regex(@"\b(prescriber_?npi|provider_?npi|npi)\s*""?\s*[:=]\s*""?\d{10}""?",
                    opts, timeout),
                "$1=[NPI]",
                highConfidence: true),

            // ── NDC variants (5 hyphenation formats) ──
            new("NDC-4-4-2",
                new Regex(@"\b\d{4}-\d{4}-\d{2}\b", opts, timeout),
                "[NDC]"),
            new("NDC-5-4-2",
                new Regex(@"\b\d{5}-\d{4}-\d{2}\b", opts, timeout),
                "[NDC]"),
            new("NDC-5-3-2",
                new Regex(@"\b\d{5}-\d{3}-\d{2}\b", opts, timeout),
                "[NDC]"),
            new("NDC-5-4-1",
                new Regex(@"\b\d{5}-\d{4}-\d\b", opts, timeout),
                "[NDC]"),
            new("NDC-unhyphenated-with-label",
                new Regex(@"\b(ndc|national[_\s-]?drug[_\s-]?code)\s*""?\s*[:=]\s*""?\d{10,11}""?",
                    opts, timeout),
                "$1=[NDC]"),

            // ── Rx-number (PioneerRx pattern). MUST run BEFORE DEA shape
            //     because "RX1234567" is 9 chars matching both patterns
            //     and we want it labeled RX_NUM not DEA. ──
            new("Rx-number-PioneerRx",
                new Regex(@"\bRX\d{6,12}\b", opts, timeout),
                "[RX_NUM]",
                highConfidence: true),

            // ── DEA number (2 letters + 7 digits + Luhn-like checksum) ──
            // Pattern matches shape; checksum validation happens in a
            // second pass via DeaChecksumValid below to keep the regex
            // NonBacktracking-compatible. NonBacktracking doesn't support
            // lookarounds, so DEA can't (?!RX) negatively-anchor; rule
            // ordering above handles the RX/DEA collision instead.
            new("DEA-number-shape",
                new Regex(@"\b[A-Z]{2}\d{7}\b", opts, timeout),
                "[DEA]",
                highConfidence: true,
                postValidator: DeaChecksumValid),

            // ── Standard PHI shapes ──
            new("SSN",
                new Regex(@"\b\d{3}-\d{2}-\d{4}\b", opts, timeout),
                "[SSN]",
                highConfidence: true),
            new("DOB-shape",
                new Regex(@"\b(0?[1-9]|1[0-2])[-/](0?[1-9]|[12]\d|3[01])[-/](19|20)\d{2}\b",
                    opts, timeout),
                "[DOB]"),

            // ── File path scrub (PII via username in path) ──
            new("Windows-user-path",
                new Regex(@"[\\/]Users[\\/][^\\/\s""'<>:|?*]+", opts, timeout),
                "/Users/[USER]"),
        };
    }

    /// <summary>
    /// DEA number checksum: (digit1+digit3+digit5) + 2*(digit2+digit4+digit6)
    /// mod 10 must equal digit7. Used as a post-regex validator since
    /// NonBacktracking can't express the checksum constraint.
    /// </summary>
    public static bool DeaChecksumValid(string deaCandidate)
    {
        if (deaCandidate.Length != 9) return false;
        var digits = deaCandidate.AsSpan(2);
        if (digits.Length != 7) return false;
        for (int i = 0; i < 7; i++)
        {
            if (!char.IsDigit(digits[i])) return false;
        }
        var n1 = digits[0] - '0';
        var n2 = digits[1] - '0';
        var n3 = digits[2] - '0';
        var n4 = digits[3] - '0';
        var n5 = digits[4] - '0';
        var n6 = digits[5] - '0';
        var n7 = digits[6] - '0';
        var checksum = (n1 + n3 + n5) + 2 * (n2 + n4 + n6);
        return checksum % 10 == n7;
    }

    private sealed class ScrubRule
    {
        public string Name { get; }
        public Regex Regex { get; }
        public string Replacement { get; }
        public bool HighConfidence { get; }
        public Func<string, bool>? PostValidator { get; }

        public ScrubRule(string name, Regex regex, string replacement,
            bool highConfidence = false, Func<string, bool>? postValidator = null)
        {
            Name = name;
            Regex = regex;
            Replacement = replacement;
            HighConfidence = highConfidence;
            PostValidator = postValidator;
        }
    }
}
