using System.Text.RegularExpressions;

namespace SuavoAgent.Diagnostics.Phi;

/// <summary>
/// Which regex engine a catalog rule is compiled with when an engine materializes it.
/// </summary>
public enum PhiEngineClass
{
    /// <summary>
    /// The .NET backtracking engine. Required for lookbehind / lookahead / backreference
    /// constructs. Every rule compiled this way on a hostile-input path MUST carry a
    /// per-rule <c>matchTimeout</c> and a fail-closed catch (blueprint fix #4).
    /// </summary>
    Standard,

    /// <summary>
    /// The linear-time <see cref="RegexOptions.NonBacktracking"/> engine. ReDoS-immune,
    /// so it is the ONLY engine allowed on the untrusted Sentry crash-text path. Does not
    /// support lookarounds or backreferences.
    /// </summary>
    NonBacktracking,
}

/// <summary>
/// Which scrub surface(s) a catalog rule is materialized into. Recorded for documentation
/// and audit: the trusted surface is <c>PhiTextScrubber.ScrubText</c> (on-box, structured,
/// full-fidelity Standard engine); the untrusted surface is
/// <c>Diagnostics.PhiScrubber.Sanitize</c> (hostile crash text → Sentry, NonBacktracking only).
/// </summary>
[System.Flags]
public enum PhiTrustScope
{
    Trusted = 1,
    Untrusted = 2,
    Both = Trusted | Untrusted,
}

/// <summary>
/// Post-regex validator gate. NonBacktracking cannot express checksum constraints, so the
/// shape is matched by regex and the candidate confirmed by this validator before redaction.
/// </summary>
public enum PhiPostValidator
{
    None,

    /// <summary>
    /// DEA number checksum: (d1+d3+d5) + 2*(d2+d4+d6) mod 10 == d7.
    /// </summary>
    DeaChecksum,
}

/// <summary>
/// A rule materialized into the TRUSTED <c>ScrubText</c> pipeline. Mirrors, one-for-one,
/// the behavior of the legacy <c>SuavoAgent.Core.Learning.PhiScrubber</c> hand-written
/// <c>[GeneratedRegex]</c> partials so the byte-exact characterization gate stays green.
/// </summary>
/// <param name="Name">Stable rule identifier (used in shadow-block telemetry, never PHI).</param>
/// <param name="Pattern">The regex source. Shared shapes (SSN, DEA) reference a single const.</param>
/// <param name="Options">Exact per-rule <see cref="RegexOptions"/> (fix #1). The engine adds
/// <see cref="RegexOptions.Compiled"/>; it never adds <see cref="RegexOptions.NonBacktracking"/>
/// on the trusted path.</param>
/// <param name="Replacement">Whole-match replacement (when <paramref name="ReplaceGroup1"/> is
/// false) OR the label that replaces capture group 1 inside the match (when true).</param>
/// <param name="ReplaceGroup1">True for the four context-name / ICD rules whose structural
/// keyword prefix must survive and only group 1 is redacted; false for whole-match rules.</param>
public sealed record TrustedRuleSpec(
    string Name,
    string Pattern,
    RegexOptions Options,
    string Replacement,
    bool ReplaceGroup1);

/// <summary>
/// A rule materialized into the UNTRUSTED <c>Sanitize</c> pipeline. Mirrors, one-for-one,
/// the behavior of the legacy <c>SuavoAgent.Diagnostics.PhiScrubber.BuildRules</c> rules.
/// </summary>
/// <param name="Name">Stable rule identifier (also surfaced in shadow-denylist telemetry).</param>
/// <param name="Pattern">The regex source. Shared shapes reference the same const as Trusted.</param>
/// <param name="Options">Semantic options WITHOUT the engine flag; the engine ORs in
/// <see cref="RegexOptions.NonBacktracking"/> when <paramref name="Engine"/> is NonBacktracking.</param>
/// <param name="Engine">NonBacktracking for every rule except the XML matching-tag rule, which
/// needs a backreference and therefore falls back to the Standard engine (with a timeout).</param>
/// <param name="Replacement">Replacement template, may contain <c>$1</c> for the captured field.</param>
/// <param name="HighConfidence">Whether this rule is part of the residual-PHI check
/// (<c>IsDefinitelyPhi</c>).</param>
/// <param name="PostValidator">Optional checksum gate (DEA) — only redacts on a valid match.</param>
public sealed record UntrustedRuleSpec(
    string Name,
    string Pattern,
    RegexOptions Options,
    PhiEngineClass Engine,
    string Replacement,
    bool HighConfidence,
    PhiPostValidator PostValidator);
