using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SuavoAgent.Diagnostics.Phi;

/// <summary>
/// The TRUSTED-path PHI engine: the single materialization of <see cref="PhiRuleCatalog.Trusted"/>.
/// On-box, structured, full-fidelity. Backs the <c>SuavoAgent.Core.Learning.PhiScrubber</c> façade,
/// so every legacy call site (~24 of them) keeps compiling unchanged.
///
/// <para><b>ReDoS (blueprint fix #4).</b> The legacy Core scrubber compiled its address/name
/// lookbehind rules with NO timeout — a crafted long no-delimiter string could hang the trusted
/// thread. Every rule here is compiled with a per-rule <see cref="_ruleTimeout"/> and every
/// <c>Replace</c>/<c>IsMatch</c> is wrapped fail-closed: on <see cref="RegexMatchTimeoutException"/>
/// the scrubber returns the <see cref="ScrubTimeoutSentinel"/> (drops all content — never leaks the
/// un-redacted value) and the PHI-present guard returns <c>true</c>.</para>
/// </summary>
public static class PhiTextScrubber
{
    /// <summary>
    /// Per-rule regex deadline. A ReDoS backstop, NOT a tight perf bound: real OCR/window-title
    /// strings finish in microseconds, so 1s only ever trips on genuine catastrophic backtracking
    /// (which runs effectively forever). Deliberately generous so a cold/slow CI runner's first
    /// match never spuriously trips it and fails closed on legitimate input.
    /// </summary>
    private static readonly TimeSpan _ruleTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Fail-closed sentinel returned by <see cref="ScrubText"/> on a rule timeout.</summary>
    public const string ScrubTimeoutSentinel = "[SCRUB_TIMEOUT]";

    private readonly record struct CompiledTrustedRule(TrustedRuleSpec Spec, Regex Regex);
    private readonly record struct CompiledShadowRule(string Name, Regex Regex, Func<string, bool>? Validator);

    private static readonly CompiledTrustedRule[] _trusted = BuildTrusted();
    private static readonly CompiledShadowRule[] _shadow = BuildShadow();

    private static CompiledTrustedRule[] BuildTrusted()
    {
        var rules = new CompiledTrustedRule[PhiRuleCatalog.Trusted.Count];
        for (var i = 0; i < rules.Length; i++)
        {
            var spec = PhiRuleCatalog.Trusted[i];
            // Standard (backtracking) engine + per-rule timeout. NonBacktracking is never added
            // here. Deliberately NOT RegexOptions.Compiled: the legacy Core rules were
            // source-generated ([GeneratedRegex], compiled at build time with no runtime cost),
            // whereas runtime Compiled pays a cold reflection-emit JIT on FIRST match. That cold
            // JIT, on a slow/cold runner, can push the first match past the per-rule deadline and
            // fail closed on legitimate input (observed in CI: ContainsPhi → true, ScrubText →
            // sentinel). The interpreted engine has no such cold spike, so the deadline cleanly
            // bounds matching time only; per-match cost on these short PHI strings is negligible.
            var regex = new Regex(spec.Pattern, spec.Options, _ruleTimeout);
            rules[i] = new CompiledTrustedRule(spec, regex);
        }
        return rules;
    }

    private static CompiledShadowRule[] BuildShadow()
    {
        var rules = new CompiledShadowRule[PhiRuleCatalog.Untrusted.Count];
        for (var i = 0; i < rules.Length; i++)
        {
            var spec = PhiRuleCatalog.Untrusted[i];
            var options = spec.Options;
            if (spec.Engine == PhiEngineClass.NonBacktracking)
                options |= RegexOptions.NonBacktracking;
            rules[i] = new CompiledShadowRule(
                spec.Name,
                new Regex(spec.Pattern, options, _ruleTimeout),
                PhiValidators.Resolve(spec.PostValidator));
        }
        return rules;
    }

    /// <summary>
    /// Returns a PHI-scrubbed copy of <paramref name="text"/>. Null in → null out. On a rule
    /// timeout, returns <see cref="ScrubTimeoutSentinel"/> (fail-closed).
    /// </summary>
    public static string? ScrubText(string? text)
    {
        if (text is null) return null;

        var result = text;
        foreach (var (spec, regex) in _trusted)
        {
            try
            {
                if (spec.ReplaceGroup1)
                {
                    // Keep the structural keyword prefix; redact only capture group 1.
                    result = regex.Replace(result, m =>
                    {
                        if (m.Groups.Count > 1 && m.Groups[1].Success)
                        {
                            var phi = m.Groups[1].Value;
                            return m.Value.Replace(phi, spec.Replacement);
                        }
                        return PhiRuleCatalog.RedactedLabel;
                    });
                }
                else
                {
                    result = regex.Replace(result, spec.Replacement);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Fail-closed: drop everything rather than risk leaking the value the
                // timed-out rule was meant to redact.
                return ScrubTimeoutSentinel;
            }
        }
        return result;
    }

    /// <summary>
    /// High-recall PHI-present test (the EnforcedDenylist behind <c>OutboundPhiGuard</c>).
    /// Exactly the legacy Core pattern set. On a rule timeout, fail-closed to <c>true</c>.
    /// </summary>
    public static bool ContainsPhi(string text)
    {
        foreach (var (_, regex) in _trusted)
        {
            try
            {
                if (regex.IsMatch(text)) return true;
            }
            catch (RegexMatchTimeoutException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// ShadowDenylist scan (blueprint fix #5). Returns the NAME of the first untrusted-origin
    /// (Diagnostics) rule that WOULD redact <paramref name="text"/>, or null if none. Used only
    /// in shadow mode to measure staged NDC/DOB/path/PioneerRx coverage on a real pilot before
    /// enforcement — the rule name is operational metadata, never PHI. Uses the ReDoS-immune
    /// NonBacktracking compilations; a per-rule timeout is treated as no-match (non-enforcing).
    /// </summary>
    public static string? ShadowDenylistMatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        foreach (var rule in _shadow)
        {
            try
            {
                if (rule.Validator is null)
                {
                    if (rule.Regex.IsMatch(text)) return rule.Name;
                }
                else
                {
                    // Checksum-gated rule (DEA): only "would fire" if a shape match also
                    // passes the validator — mirrors the engine's redact decision.
                    for (var m = rule.Regex.Match(text); m.Success; m = m.NextMatch())
                    {
                        if (rule.Validator(m.Value)) return rule.Name;
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // Shadow is a measurement, not a gate — a timeout must not block the payload.
                // Skip this rule.
            }
        }
        return null;
    }

    /// <summary>
    /// HMAC-SHA256 with a per-pharmacy salt. Not dictionary-attackable like plain SHA-256.
    /// </summary>
    public static string HmacHash(string value, string salt)
    {
        var key = Encoding.UTF8.GetBytes(salt);
        var data = Encoding.UTF8.GetBytes(value);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
