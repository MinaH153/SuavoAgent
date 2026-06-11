using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.Agentic;

/// <summary>Result of certifying a string/step for banking. <see cref="RefusalReason"/> is an
/// operational CODE (category + optional param key / rule name) — NEVER the offending value, so the
/// reason itself can be logged and shipped in telemetry without re-leaking what it refused.</summary>
public readonly record struct HarvestCertification(bool Certified, string? RefusalReason)
{
    public static readonly HarvestCertification Ok = new(true, null);
    public static HarvestCertification Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Phase-3B scrubbing-aware harvest certification: decides whether ONE verified step's action (verb +
/// params + signature) is PROVABLY free of PHI per the trusted scrub catalog and therefore safe to bank
/// verbatim into <c>verified_skills</c>. This is the gate that lifts the navigate-path hard-stop (the old
/// click-only allowlist) without ever letting patient data into a banked skill.
///
/// <para><b>Certify-or-refuse, never transform.</b> A banked action must replay EXACTLY
/// (<see cref="VerifiedSkillReplayer"/> rejects any step whose reconstructed signature differs from the
/// banked one), so a scrubbed-but-different param is useless AND dangerous — it would type a redaction
/// sentinel into a live field. The scrubber is therefore used as a CERTIFIER: a value is bankable only
/// when scrubbing it is a no-op (<c>ScrubText(v) == v</c> and <c>!ContainsPhi(v)</c>). Anything the
/// catalog would redact refuses the value — and the harvester then refuses the WHOLE trajectory.</para>
///
/// <para><b>Threat model.</b> The perceived screen is already scrubbed (<c>AssertScrubbed</c>), so the
/// only paths real patient data can take into action params are (a) the free-form objective Goal
/// (operator-supplied — "find patient Smith" → the reasoner types "Smith"), and (b) raw UI labels echoed
/// by the LLM from the goal rather than the scrubbed screen. Hence the layered standard:</para>
///
/// <list type="bullet">
/// <item><b>Every banked string</b> (verb, signature, param keys + values, serialized steps JSON):
/// trusted-catalog certification — <c>ContainsPhi</c> false AND scrub-idempotent. Fail-closed on
/// scrub timeout/sentinel/exception.</item>
/// <item><b>Free-text values</b> (typed text — the NEW surface this phase opens): additionally the
/// ShadowDenylist (NDC / DOB / PioneerRx-id / member-id staged rules — enforced here even though
/// shadow elsewhere), a length cap, a ≥6-digit-run veto (Rx#/MRN/member-id shaped numerics), a
/// name-shape veto (two consecutive capitalized words), and a goal-echo veto (any alpha token ≥3 chars
/// shared with the Goal). Goal-echoed text is refused for a second, independent reason: it is
/// run-specific by construction, so banking it as a literal would replay a stale value — those are
/// the holes of a future parameterized template, not constants.</item>
/// <item><b>Chords</b> (press_keys): must parse under a conservative chord grammar; ≤2 single-letter
/// main keys per step so a name cannot be spelled letter-by-letter.</item>
/// <item><b>Structural params / UI labels</b>: trusted-catalog certification (the shipped click
/// standard, unchanged — labels normally come from the scrubbed screen's vocabulary).</item>
/// <item><b>Verb allowlist</b>: only verbs the replayer + gates handle deterministically. Free-text
/// verbs REQUIRE structured params (Verb + ActionParams) — a sig-only type/press step cannot be
/// certified value-by-value and is refused.</item>
/// </list>
///
/// Pure logic, no IO; every regex is NonBacktracking (ReDoS-immune). Any unexpected exception
/// certifies FALSE (fail-closed).
/// </summary>
public static class HarvestPhiCertifier
{
    /// <summary>Verbs bankable from a verified trajectory. Anything else refuses the trajectory.</summary>
    private static readonly HashSet<string> ClickFamilyVerbs = new(StringComparer.Ordinal)
    {
        "click_by_label", "click_by_signature", "launch_sandbox_app",
    };

    /// <summary>Free-text verbs (the Phase-3B lift). Structured params are REQUIRED for these.</summary>
    private static readonly HashSet<string> FreeTextVerbs = new(StringComparer.Ordinal)
    {
        "type_into_field", "press_keys",
    };

    /// <summary>Param keys whose values are bounded structural vocabulary (process names, app keys,
    /// UIA signatures) — trusted-catalog standard. Everything NOT listed here on a free-text verb is
    /// held to the strictest (free-text) standard, fail-closed.</summary>
    private static readonly HashSet<string> StructuralParamKeys = new(StringComparer.Ordinal)
    {
        "process_name", "app_key", "signature", "control_type", "automation_id", "label",
    };

    private const int MaxFreeTextLength = 256;
    private const int MaxChords = 16;
    private const int MaxSingleLetterChordMains = 2;

    // Two consecutive capitalized alpha words — the bare-name shape the regex catalog's
    // context-anchored rules miss ("John Doe" with no "Patient:" prefix).
    private static readonly Regex NameShape = new(
        @"\b[A-Z][A-Za-z']+[ \t]+[A-Z][A-Za-z']+\b",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    // Identifier-shaped numerics: any unbroken run of 6+ digits (Rx numbers, MRNs, member IDs typed
    // bare into a search field carry no catalog context keyword — refuse by shape).
    private static readonly Regex LongDigitRun = new(
        @"\d{6,}", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    // Values that are obviously non-identifying: booleans and short numeric/punctuation strings
    // (prices, quantities, percentages). The trusted+shadow scans still run FIRST, so date / SSN /
    // phone / NDC shaped strings never reach this fast-pass.
    private static readonly Regex BoolValue = new(
        @"^(?i:true|false)$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private static readonly Regex NumericValue = new(
        @"^[0-9 .,%$/()+-]{1,64}$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    private static readonly Regex AlphaToken = new(
        @"[A-Za-z]{3,}", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    // Verb names are snake_case identifiers; anything else is malformed and refused.
    private static readonly Regex VerbShape = new(
        @"^[a-z0-9_]{1,64}$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>Glue words excluded from the goal-echo veto so ordinary instructions ("open the
    /// pricing tab") don't block values that merely share English plumbing. Kept SMALL on purpose:
    /// a missing entry over-refuses (safe); an extra entry could mask a real echo.</summary>
    private static readonly HashSet<string> GoalEchoStoplist = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "then", "this", "that", "from", "into", "when", "each",
        "open", "click", "type", "press", "enter", "select", "search", "find", "save", "close",
        "set", "use", "using", "respond", "action", "parameter", "status", "complete", "objective",
    };

    /// <summary>Named chord keys mirrored from the Helper's KeyChord token map (certification-side
    /// allowlist — drift here can only OVER-refuse, never under).</summary>
    private static readonly HashSet<string> NamedChordKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "esc", "escape", "enter", "return", "tab", "space", "back", "backspace", "delete", "del",
        "home", "end", "pageup", "pgup", "pagedown", "pgdn", "left", "right", "up", "down",
    };

    private static readonly HashSet<string> ChordModifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ctrl", "control", "shift", "alt", "win", "meta", "lwin",
    };

    /// <summary>
    /// Certify one verified step for banking. <paramref name="goal"/> is the objective's free-form
    /// goal text (the PHI provenance channel the goal-echo veto checks against).
    /// </summary>
    public static HarvestCertification CertifyStep(StepRecord step, string goal)
    {
        try
        {
            return CertifyStepCore(step, goal ?? string.Empty);
        }
        catch (Exception)
        {
            // Fail-closed: an unexpected certification fault must never default to "bankable".
            return HarvestCertification.Refuse("certifier_exception");
        }
    }

    /// <summary>
    /// End-of-pipe certification of the EXACT serialized steps_json string that will be persisted —
    /// belt-and-suspenders against PHI shapes assembled across value boundaries by serialization.
    /// </summary>
    public static HarvestCertification CertifySerializedSteps(string stepsJson)
    {
        try
        {
            if (string.IsNullOrEmpty(stepsJson))
                return HarvestCertification.Refuse("serialized_steps_empty");
            return TrustedClean(stepsJson)
                ? HarvestCertification.Ok
                : HarvestCertification.Refuse("serialized_steps_trusted_phi");
        }
        catch (Exception)
        {
            return HarvestCertification.Refuse("certifier_exception");
        }
    }

    private static HarvestCertification CertifyStepCore(StepRecord step, string goal)
    {
        // 1. Resolve + allowlist the verb. Structured verb wins; a sig-only row falls back to the
        //    signature's verb prefix. Unknown/malformed verb → refuse.
        var verb = !string.IsNullOrEmpty(step.ActionVerb) ? step.ActionVerb : VerbFromSignature(step.ActionSignature);
        if (verb is null || !VerbShape.IsMatch(verb))
            return HarvestCertification.Refuse("verb_unresolvable");
        // Defense-in-depth: the structured verb must agree with the signature's verb prefix (they are
        // built from the SAME NextAction in ContextAccumulator — a divergence means a forged/corrupt
        // step that could smuggle a free-text action under the click standard). Refuse, never guess.
        if (VerbFromSignature(step.ActionSignature) is { } sigVerb && !string.Equals(sigVerb, verb, StringComparison.Ordinal))
            return HarvestCertification.Refuse("verb_signature_mismatch");
        var isFreeTextVerb = FreeTextVerbs.Contains(verb);
        if (!ClickFamilyVerbs.Contains(verb) && !isFreeTextVerb)
            return HarvestCertification.Refuse($"verb_not_bankable:{verb}");

        // 2. Free-text verbs (type/press) are certifiable only value-by-value: structured params are
        //    REQUIRED. A sig-only type/press step is refused — the unescaped signature cannot be
        //    split back into exact values, so per-value certification is impossible.
        if (isFreeTextVerb && step.ActionParams is not { Count: > 0 })
            return HarvestCertification.Refuse($"structured_params_required:{verb}");

        // 3. Certify every param key + value under its class standard (before the whole-signature scan,
        //    so a dirty value surfaces a pinpoint param-level reason).
        if (step.ActionParams is { } ps)
        {
            foreach (var (key, value) in ps)
            {
                if (string.IsNullOrEmpty(key) || !TrustedClean(key))
                    return HarvestCertification.Refuse("param_key_trusted_phi");
                var v = value ?? string.Empty;

                if (!TrustedClean(v))
                    return HarvestCertification.Refuse($"param_trusted_phi:{key}");

                if (verb == "press_keys" && key == "chords")
                {
                    var chords = CertifyChords(v);
                    if (!chords.Certified) return chords;
                    continue;
                }

                // Click-family verbs keep the shipped structural/label standard. On free-text verbs,
                // only known structural keys keep it — every other value (the typed text itself, and
                // any unknown key, fail-closed) is held to the strictest free-text standard.
                if (!isFreeTextVerb || StructuralParamKeys.Contains(key))
                    continue;

                var free = CertifyFreeText(v, key, goal);
                if (!free.Certified) return free;
            }
        }

        // 4. The signature is banked verbatim → trusted-catalog certification ALWAYS (this is also
        //    the only value-bearing scan available for legacy sig-only click rows — Codex Q3).
        if (!TrustedClean(step.ActionSignature))
            return HarvestCertification.Refuse("signature_trusted_phi");

        return HarvestCertification.Ok;
    }

    /// <summary>Trusted-catalog certification: scrubbing the string is a no-op. Both probes fail
    /// closed internally (timeout → ContainsPhi=true / ScrubText=sentinel → not idempotent).</summary>
    private static bool TrustedClean(string value)
    {
        if (value.Length == 0) return true;
        if (PhiScrubber.ContainsPhi(value)) return false;
        return string.Equals(PhiScrubber.ScrubText(value), value, StringComparison.Ordinal);
    }

    private static HarvestCertification CertifyFreeText(string value, string key, string goal)
    {
        if (value.Length == 0) return HarvestCertification.Ok;
        if (value.Length > MaxFreeTextLength)
            return HarvestCertification.Refuse($"free_text_too_long:{key}");

        // Staged denylist (NDC / DOB shape / PioneerRx ids / member ids): shadow-mode elsewhere,
        // ENFORCED on this brand-new surface. Rule name is operational metadata, never PHI.
        if (PhiScrubber.ShadowDenylistMatch(value) is { } rule)
            return HarvestCertification.Refuse($"free_text_shadow_denylist:{key}:{rule}");

        if (LongDigitRun.IsMatch(value))
            return HarvestCertification.Refuse($"free_text_identifier_digits:{key}");

        // Obvious non-identifying values (prices, quantities, flags) — already past the catalog scans.
        if (BoolValue.IsMatch(value) || NumericValue.IsMatch(value))
            return HarvestCertification.Ok;

        if (NameShape.IsMatch(value))
            return HarvestCertification.Refuse($"free_text_name_shape:{key}");

        if (SharesGoalToken(value, goal))
            return HarvestCertification.Refuse($"free_text_goal_echo:{key}");

        return HarvestCertification.Ok;
    }

    /// <summary>True when the value shares any non-stoplist alpha token (≥3 chars) with the goal —
    /// the provenance channel for real patient data, and the signature of a run-specific literal.</summary>
    private static bool SharesGoalToken(string value, string goal)
    {
        if (goal.Length == 0) return false;
        var goalTokens = new HashSet<string>(
            AlphaToken.Matches(goal).Select(m => m.Value.ToLowerInvariant())
                .Where(t => !GoalEchoStoplist.Contains(t)),
            StringComparer.Ordinal);
        if (goalTokens.Count == 0) return false;
        return AlphaToken.Matches(value)
            .Select(m => m.Value.ToLowerInvariant())
            .Any(t => !GoalEchoStoplist.Contains(t) && goalTokens.Contains(t));
    }

    /// <summary>Chords must parse under the conservative chord grammar (modifiers + ONE main key per
    /// chord), with a hard cap on single-letter mains so PHI cannot be spelled key-by-key.</summary>
    private static HarvestCertification CertifyChords(string value)
    {
        var tokens = value.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return HarvestCertification.Refuse("chords_empty");
        if (tokens.Length > MaxChords)
            return HarvestCertification.Refuse("chords_too_many");

        var singleLetterMains = 0;
        foreach (var token in tokens)
        {
            var parts = token.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
                return HarvestCertification.Refuse("chords_malformed");
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!ChordModifiers.Contains(parts[i]))
                    return HarvestCertification.Refuse("chords_bad_modifier");
            }
            var main = parts[^1];
            var isSingleAlnum = main.Length == 1 && char.IsLetterOrDigit(main[0]);
            var isFunctionKey = main.Length is 2 or 3 && (main[0] is 'F' or 'f')
                && int.TryParse(main[1..], out var fn) && fn is >= 1 and <= 12;
            if (!isSingleAlnum && !isFunctionKey && !NamedChordKeys.Contains(main))
                return HarvestCertification.Refuse("chords_bad_key");
            if (main.Length == 1 && char.IsLetter(main[0]) && ++singleLetterMains > MaxSingleLetterChordMains)
                return HarvestCertification.Refuse("chords_spelling_veto");
        }

        return HarvestCertification.Ok;
    }

    private static string? VerbFromSignature(string signature)
    {
        var open = signature.IndexOf('(');
        return open > 0 && signature[^1] == ')' ? signature[..open] : null;
    }
}
