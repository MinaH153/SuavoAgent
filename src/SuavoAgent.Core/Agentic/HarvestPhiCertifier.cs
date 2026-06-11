using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.Agentic;

/// <summary>Result of certifying a string/step/trajectory for banking. <see cref="RefusalReason"/> is an
/// operational CODE (category + optional param key / rule name) — NEVER the offending value, so the
/// reason itself can be logged and shipped in telemetry without re-leaking what it refused.</summary>
public readonly record struct HarvestCertification(bool Certified, string? RefusalReason)
{
    public static readonly HarvestCertification Ok = new(true, null);
    public static HarvestCertification Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Phase-3B scrubbing-aware harvest certification: decides whether a verified trajectory's actions
/// (verbs + params + signatures) are PROVABLY free of PHI per the trusted scrub catalog and therefore
/// safe to bank verbatim into <c>verified_skills</c>. This is the gate that lifts the navigate-path
/// hard-stop (the old click-only allowlist) without ever letting patient data into a banked skill.
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
/// by the LLM from the goal rather than the scrubbed screen.</para>
///
/// <para><b>Two-pass certification (Codex HIPAA round 2).</b> Per-step checks alone leak a name/SSN
/// SPLIT across steps (<c>type "Jo"</c> + <c>type "hn"</c>, or <c>press J</c> + <c>press o</c>…). So:</para>
/// <list type="number">
/// <item><b>Per-step structural pass</b> (<see cref="CertifyStep"/>): verb allowlist; full
/// <c>NextAction.Act(verb, params).Signature() == ActionSignature</c> reconstruction equality (a forged
/// ParamsJson that disagrees with the banked signature is refused — the same identity the replayer
/// enforces, pulled forward to harvest so dirty params never persist); VERB-SCOPED structural keys
/// (<c>type_into_field</c> requires <c>text</c> and rejects <c>label</c>/<c>signature</c>/automation —
/// closing structural-key smuggling); trusted-catalog certification of the signature + every param
/// key/value; chord grammar.</item>
/// <item><b>Trajectory keyboard-stream pass</b> (<see cref="CertifyTrajectory"/>): concatenate the
/// CONTIGUOUS keyboard output (typed text + single-letter chord mains) across steps and run the FULL
/// free-text vetoes (catalog, shadow denylist, email, total-digit, name-shape, goal-echo) on the
/// concatenation — so a name/identifier assembled key-by-key is caught.</item>
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

    /// <summary>
    /// VERB-SCOPED structural-param allowlist (Codex #4). A param key keeps the bounded-structural
    /// (trusted-catalog-only) standard ONLY for the verbs that legitimately carry it. Anything not
    /// listed for a verb is held to the strictest free-text standard, fail-closed — so e.g.
    /// <c>type_into_field(label="Jane Doe")</c> can no longer smuggle a name through the globally
    /// "structural" <c>label</c> key.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, HashSet<string>> StructuralKeysByVerb =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["click_by_label"] = new(StringComparer.Ordinal) { "label", "process_name", "app_key" },
            ["click_by_signature"] = new(StringComparer.Ordinal) { "signature", "control_type", "automation_id", "process_name", "app_key" },
            ["launch_sandbox_app"] = new(StringComparer.Ordinal) { "app_key", "process_name" },
            // type_into_field: NO label/signature/automation here. Only the bounded typed-control knobs.
            // The value-bearing `text` is deliberately ABSENT → always held to the free-text standard.
            ["type_into_field"] = new(StringComparer.Ordinal) { "clear_first", "per_key_delay_ms", "process_name" },
            // press_keys: only the inter-chord delay is structural; `chords` is grammar-checked, not here.
            ["press_keys"] = new(StringComparer.Ordinal) { "inter_chord_delay_ms", "process_name" },
        };

    /// <summary>The single required value-bearing param per free-text verb (Codex #4). Absence → refuse
    /// (a free-text verb with no certifiable payload is malformed).</summary>
    private static readonly IReadOnlyDictionary<string, string> RequiredFreeTextKey =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type_into_field"] = "text",
            ["press_keys"] = "chords",
        };

    private const int MaxFreeTextLength = 256;
    private const int MaxChords = 16;
    private const int MaxStreamSingleLetters = 2;     // trajectory-wide letter-spelling cap (Codex #1/#7)
    private const int MaxFreeTextTotalDigits = 5;     // ≥6 total digits (separators stripped) → refuse (Codex #2)

    // Two consecutive capitalized alpha words — the bare-name shape the regex catalog's
    // context-anchored rules miss ("John Doe" with no "Patient:" prefix). Folded text is lower-cased,
    // so this runs on the ORIGINAL value (before folding) for the name SHAPE.
    private static readonly Regex NameShape = new(
        @"\b\p{Lu}[\p{L}']+[ \t]+\p{Lu}[\p{L}']+\b",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    // Booleans and short numeric/punctuation strings (prices, quantities, percentages). The
    // catalog/shadow/email/total-digit scans all run FIRST, so SSN/date/NDC/identifier shapes never
    // reach this fast-pass.
    private static readonly Regex BoolValue = new(
        @"^(?i:true|false)$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private static readonly Regex NumericValue = new(
        @"^[0-9 .,%$/()+-]{1,64}$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    // Email — typed contact PHI the trusted catalog does not cover (Codex #6). Conservative shape.
    private static readonly Regex EmailShape = new(
        @"[\w.+\-]+@[\w\-]+\.[\w.\-]+", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    // Unicode-aware alpha token (≥3 LETTERS, any script) for the goal-echo compare. Operates on
    // diacritic-FOLDED, case-folded text so "García"→"garcia" matches typed "garcia" (Codex #3).
    private static readonly Regex UnicodeAlphaToken = new(
        @"\p{L}{3,}", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    // Verb names are snake_case identifiers; anything else is malformed and refused.
    private static readonly Regex VerbShape = new(
        @"^[a-z0-9_]{1,64}$", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    /// <summary>Glue words excluded from the goal-echo veto so ordinary instructions ("open the
    /// pricing tab") don't block values that merely share English plumbing. Kept SMALL on purpose:
    /// a missing entry over-refuses (safe); an extra entry could mask a real echo. Folded form.</summary>
    private static readonly HashSet<string> GoalEchoStoplist = new(StringComparer.Ordinal)
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

    // ───────────────────────────── per-step structural pass ─────────────────────────────

    /// <summary>
    /// Certify one step's STRUCTURE for banking (verb allowlist, reconstruction equality, verb-scoped
    /// structural keys, catalog scans, chord grammar). <paramref name="goal"/> seeds the per-step
    /// free-text vetoes for NON-keyboard free text (the keyboard stream is certified separately, across
    /// steps, by <see cref="CertifyTrajectory"/>). Returns the first refusal, fail-closed on exception.
    /// </summary>
    public static HarvestCertification CertifyStep(StepRecord step, string goal)
    {
        try
        {
            return CertifyStepCore(step, goal ?? string.Empty);
        }
        catch (Exception)
        {
            return HarvestCertification.Refuse("certifier_exception");
        }
    }

    /// <summary>
    /// Certify a whole trajectory: every step's structure PLUS the cross-step keyboard stream. This is
    /// the entry point the harvester calls. <paramref name="steps"/> is the ordered Met/Success step
    /// list already filtered by the harvester; the index in a refusal code is the position WITHIN that
    /// filtered list. Fail-closed on any exception.
    /// </summary>
    public static HarvestCertification CertifyTrajectory(IReadOnlyList<StepRecord> steps, string goal)
    {
        try
        {
            goal ??= string.Empty;

            // Pass 1: per-step structure.
            for (var i = 0; i < steps.Count; i++)
            {
                var cert = CertifyStepCore(steps[i], goal);
                if (!cert.Certified)
                    return HarvestCertification.Refuse($"step{i}:{cert.RefusalReason}");
            }

            // Pass 2: keyboard-stream vetoes over the CONCATENATION of typed text + chord letters
            // (Codex #1/#2/#7). TWO scopes, both fail-closed:
            //   (a) per contiguous RUN (segmented by clicks) — the full free-text stack on a logical
            //       field entry, where space-joined structure (NameShape, identifier digits typed into
            //       ONE field) is meaningful.
            //   (b) the WHOLE trajectory (clicks do NOT reset it) — closes a goal-derived literal split
            //       ACROSS a click into fragments too short for any per-run veto ("Jo"<click>"hn" →
            //       "john"). On the assembled keystrokes we run the letter cap + total-digit + a SUBSTRING
            //       goal-echo: a goal token found ANYWHERE inside the keystrokes is a run-specific echo no
            //       matter where the agent clicked between them. (A name with no goal provenance leaves
            //       none — the screen is scrubbed. Substring, not token-equality, because cross-field
            //       assembly drops the separators the token form relies on.)
            var run = new StringBuilder();
            var runSingleLetters = 0;
            var full = new StringBuilder();
            var fullSingleLetters = 0;
            var keyboardRuns = 0;
            for (var i = 0; i <= steps.Count; i++)
            {
                var (isKeyboard, text, letters) = i < steps.Count ? KeyboardOutput(steps[i]) : (false, string.Empty, 0);
                if (isKeyboard)
                {
                    run.Append(text); runSingleLetters += letters;
                    full.Append(text); fullSingleLetters += letters;
                    continue;
                }
                // Run boundary (click / end): certify the accumulated contiguous run, then reset the RUN only.
                if (run.Length > 0 || runSingleLetters > 0)
                {
                    keyboardRuns++;
                    if (runSingleLetters > MaxStreamSingleLetters)
                        return HarvestCertification.Refuse($"stream{i}:keyboard_spelling_veto");
                    var runCert = CertifyFreeText(run.ToString(), "keyboard_stream", goal);
                    if (!runCert.Certified)
                        return HarvestCertification.Refuse($"stream{i}:{runCert.RefusalReason}");
                    run.Clear();
                    runSingleLetters = 0;
                }
            }

            // (b) Whole-trajectory cross-click defense — ONLY when a click actually fragmented the typed
            // stream (≥2 keyboard runs). A goal-derived name/identifier can then be split across the click
            // into fragments each too short for the per-run vetoes, so run the FULL free-text stack on the
            // concatenation (catalog / shadow / email / total-digit / name-shape / token goal-echo — the
            // SAME checks the per-run pass runs, never a subset) PLUS a separator-robust substring goal-echo
            // for fragments that reassemble only without their separators ("John"+"Doe" → "johndoe" ⊇ "john").
            // A SINGLE uninterrupted run already cleared the full stack per-run with exact token semantics, so
            // it is NOT re-checked here — substring semantics on a single run would over-refuse a legit value
            // that coincidentally contains a short goal token ("tablets" vs goal "…tab"). (2026-06-11 Fable
            // review of the cross-click fix — the first pass ran a subset and reopened email/name-shape.)
            if (keyboardRuns >= 2)
            {
                var fullText = full.ToString();
                if (fullText.Length > 0)
                {
                    if (fullSingleLetters > MaxStreamSingleLetters)
                        return HarvestCertification.Refuse("trajectory:keyboard_spelling_veto");
                    var fullCert = CertifyFreeText(fullText, "trajectory", goal);
                    if (!fullCert.Certified)
                        return HarvestCertification.Refuse($"trajectory:{fullCert.RefusalReason}");
                    if (ContainsGoalSubstring(fullText, goal))
                        return HarvestCertification.Refuse("trajectory:goal_echo");
                }
            }

            return HarvestCertification.Ok;
        }
        catch (Exception)
        {
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
        var isFreeTextVerb = FreeTextVerbs.Contains(verb);
        if (!ClickFamilyVerbs.Contains(verb) && !isFreeTextVerb)
            return HarvestCertification.Refuse($"verb_not_bankable:{verb}");

        // 2. Free-text verbs are certifiable only value-by-value: structured params are REQUIRED, and
        //    the value-bearing key (text / chords) MUST be present (Codex #4).
        if (isFreeTextVerb)
        {
            if (step.ActionParams is not { Count: > 0 })
                return HarvestCertification.Refuse($"structured_params_required:{verb}");
            if (RequiredFreeTextKey.TryGetValue(verb, out var reqKey)
                && !step.ActionParams.ContainsKey(reqKey))
                return HarvestCertification.Refuse($"free_text_payload_missing:{verb}:{reqKey}");
        }

        // 3. RECONSTRUCTION EQUALITY (Codex #5): rebuild the action from the BANKED verb + params and
        //    require its signature to equal the banked ActionSignature EXACTLY — the same identity the
        //    replayer enforces, pulled forward so a forged/mismatched ParamsJson (dirty `label` under a
        //    clean `text` signature) can never persist. Only enforceable when structured params exist;
        //    sig-only rows are click-family (free-text sig-only already refused in step 2) and fall
        //    through to the verbatim signature scan.
        if (step.ActionParams is { Count: > 0 } sp)
        {
            var rebuilt = NextAction.Act(verb, sp.ToDictionary(kv => kv.Key, kv => (object?)kv.Value));
            if (!string.Equals(rebuilt.Signature(), step.ActionSignature, StringComparison.Ordinal))
                return HarvestCertification.Refuse("signature_reconstruction_mismatch");
        }
        else if (VerbFromSignature(step.ActionSignature) is { } sigVerb && !string.Equals(sigVerb, verb, StringComparison.Ordinal))
        {
            // Sig-only row: at least the verb prefix must agree.
            return HarvestCertification.Refuse("verb_signature_mismatch");
        }

        // 4. Certify every param key + value under its VERB-SCOPED class standard.
        var structuralKeys = StructuralKeysByVerb.TryGetValue(verb, out var sk) ? sk : null;
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

                // Verb-scoped structural keys keep the trusted-catalog-only standard. Everything else
                // (the typed `text`, any key not structural FOR THIS VERB, fail-closed) gets the full
                // free-text vetoes here too — the trajectory stream pass is ADDITIONAL, not a substitute.
                //
                // NB: a goal-echo check is DELIBERATELY NOT applied to click labels. UI button labels
                // legitimately share vocabulary with the task goal ("click Price" when the goal is to
                // update a price), so echo is the NORM for labels, not a signal — it would over-refuse
                // the canonical pricing workflow. The bare-name-click-label residual is therefore left
                // wholly to Phase-3C bank-time label grounding (label ∈ scrubbed screen), which is the
                // only check that distinguishes a real UI control from an LLM-echoed name. (Verified:
                // an interim label goal-echo veto broke "update the price to 12.99" → click "Price".)
                if (structuralKeys is not null && structuralKeys.Contains(key))
                    continue;

                var free = CertifyFreeText(v, key, goal);
                if (!free.Certified) return free;
            }
        }

        // 5. The signature is banked verbatim → trusted-catalog certification ALWAYS (this is also the
        //    only value-bearing scan available for legacy sig-only click rows — Codex Q3).
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

    /// <summary>The full free-text veto stack — applied per-value AND to the cross-step keyboard
    /// stream. Order matters: high-recall catalog/shadow/email/total-digit scans run BEFORE the
    /// numeric/bool fast-pass so identifier/contact shapes can never slip through it.</summary>
    private static HarvestCertification CertifyFreeText(string value, string key, string goal)
    {
        if (value.Length == 0) return HarvestCertification.Ok;
        if (value.Length > MaxFreeTextLength)
            return HarvestCertification.Refuse($"free_text_too_long:{key}");

        if (!TrustedClean(value))
            return HarvestCertification.Refuse($"free_text_trusted_phi:{key}");

        // Staged denylist (NDC / DOB shape / PioneerRx ids / member ids): shadow-mode elsewhere,
        // ENFORCED on this brand-new surface. Rule name is operational metadata, never PHI.
        if (PhiScrubber.ShadowDenylistMatch(value) is { } rule)
            return HarvestCertification.Refuse($"free_text_shadow_denylist:{key}:{rule}");

        if (EmailShape.IsMatch(value))
            return HarvestCertification.Refuse($"free_text_email:{key}");

        // Total-digit veto (Codex #2): strip separators, count digits. ≥6 total → identifier shape
        // (SSN "123 45 6789", DOB "01.02.1990", MRN "123 456") UNLESS the value is a narrow
        // price/qty/percent (handled by the numeric fast-pass below, which a digit count ≥6 reaching
        // here has already been denied — so a 6+ digit price is refused too; acceptable, rare).
        if (CountDigits(value) > MaxFreeTextTotalDigits)
            return HarvestCertification.Refuse($"free_text_identifier_digits:{key}");

        // Obvious non-identifying values (prices, quantities, flags) — already past every shape scan.
        if (BoolValue.IsMatch(value) || NumericValue.IsMatch(value))
            return HarvestCertification.Ok;

        if (NameShape.IsMatch(value))
            return HarvestCertification.Refuse($"free_text_name_shape:{key}");

        if (SharesGoalToken(value, goal))
            return HarvestCertification.Refuse($"free_text_goal_echo:{key}");

        return HarvestCertification.Ok;
    }

    /// <summary>True when the value shares any non-stoplist alpha token (≥3 LETTERS) with the goal,
    /// compared after Unicode-normalize + diacritic-fold + case-fold on BOTH sides (Codex #3) — so a
    /// goal "find patient García" catches typed "garcia". The goal is the provenance channel for real
    /// patient data, and a goal-echoed literal is run-specific (wrong to bank) regardless.</summary>
    private static bool SharesGoalToken(string value, string goal)
    {
        if (goal.Length == 0) return false;
        var goalTokens = FoldedTokens(goal);
        if (goalTokens.Count == 0) return false;
        return FoldedTokens(value).Overlaps(goalTokens);
    }

    /// <summary>True when the assembled cross-click keystrokes CONTAIN any non-stoplist goal token
    /// (≥3 letters) as a SUBSTRING after diacritic+case fold — so a goal-derived literal is caught no
    /// matter how it was fragmented across clicks/fields ("Jo"&lt;click&gt;"hn" → "john" ⊇ goal "john").
    /// Substring (not token-equality) because cross-field assembly drops the separators the token form
    /// relies on. Over-refuses a typed value that coincidentally contains a goal token — safe (a
    /// goal-echoed literal is run-specific and shouldn't bank anyway; clean non-echoed text has none).</summary>
    private static bool ContainsGoalSubstring(string concat, string goal)
    {
        if (goal.Length == 0) return false;
        var folded = FoldDiacriticsLower(concat);
        if (folded.Length == 0) return false;
        foreach (var token in FoldedTokens(goal))
            if (folded.Contains(token, StringComparison.Ordinal)) return true;
        return false;
    }

    private static HashSet<string> FoldedTokens(string text)
    {
        var folded = FoldDiacriticsLower(text);
        return new HashSet<string>(
            UnicodeAlphaToken.Matches(folded).Select(m => m.Value).Where(t => !GoalEchoStoplist.Contains(t)),
            StringComparer.Ordinal);
    }

    /// <summary>Unicode NFD-decompose, drop combining marks (diacritic fold), lower-case invariantly.
    /// "García" → "garcia", "JOSÉ" → "jose". Defends the goal-echo compare against accent/case evasion.</summary>
    private static string FoldDiacriticsLower(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().ToLowerInvariant();
    }

    private static int CountDigits(string value)
    {
        var n = 0;
        foreach (var ch in value) if (ch is >= '0' and <= '9') n++;
        return n;
    }

    /// <summary>
    /// The keyboard OUTPUT of a step for the trajectory stream: for <c>type_into_field</c> the typed
    /// <c>text</c>; for <c>press_keys</c> the printable characters a chord produces (a single un-modified
    /// alnum main types that character — Shift/Ctrl/named keys produce no stream text but the
    /// single-LETTER count still feeds the spelling cap). Non-keyboard steps return isKeyboard=false,
    /// breaking the contiguous run.
    /// </summary>
    private static (bool IsKeyboard, string Text, int SingleLetters) KeyboardOutput(StepRecord step)
    {
        var verb = !string.IsNullOrEmpty(step.ActionVerb) ? step.ActionVerb : VerbFromSignature(step.ActionSignature);
        if (verb == "type_into_field")
        {
            var text = step.ActionParams is { } p && p.TryGetValue("text", out var t) ? t ?? string.Empty : string.Empty;
            return (true, text, 0);
        }
        if (verb == "press_keys")
        {
            var chords = step.ActionParams is { } p && p.TryGetValue("chords", out var c) ? c ?? string.Empty : string.Empty;
            var (text, letters) = ChordStreamOutput(chords);
            return (true, text, letters);
        }
        return (false, string.Empty, 0);
    }

    /// <summary>Printable stream + single-letter count from a chord list. An un-modified single
    /// alphanumeric main contributes its character to the stream; a modified or named/function main
    /// contributes nothing (navigation, not text). Conservative: malformed chords contribute nothing
    /// here — the per-step <see cref="CertifyChords"/> pass is what refuses malformed grammar.</summary>
    private static (string Text, int SingleLetters) ChordStreamOutput(string chords)
    {
        var sb = new StringBuilder();
        var letters = 0;
        foreach (var token in chords.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 1) continue; // any modifier present → not literal text output
            var main = parts[0];
            if (main.Length == 1 && char.IsLetterOrDigit(main[0]))
            {
                sb.Append(main[0]);
                if (char.IsLetter(main[0])) letters++;
            }
        }
        return (sb.ToString(), letters);
    }

    /// <summary>Chords must parse under the conservative chord grammar (modifiers + ONE main key per
    /// chord), with a hard cap on single-letter mains so PHI cannot be spelled key-by-key within ONE
    /// step. The TRAJECTORY-wide letter cap (across steps) is enforced in <see cref="CertifyTrajectory"/>.</summary>
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
            if (main.Length == 1 && char.IsLetter(main[0]) && ++singleLetterMains > MaxStreamSingleLetters)
                return HarvestCertification.Refuse("chords_spelling_veto");
        }

        return HarvestCertification.Ok;
    }

    private static string? VerbFromSignature(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        var open = signature.IndexOf('(');
        return open > 0 && signature[^1] == ')' ? signature[..open] : null;
    }
}
