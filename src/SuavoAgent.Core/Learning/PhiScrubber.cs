// src/SuavoAgent.Core/Learning/PhiScrubber.cs
using SuavoAgent.Diagnostics.Phi;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// In-memory PHI detection and scrubbing. Runs before any observation is persisted.
/// Implements observe-hash-discard per HIPAA Safe Harbor (45 CFR 164.514).
///
/// <para>As of the single-source PHI merge this is a THIN FAÇADE: every rule now lives in the
/// shared <see cref="PhiRuleCatalog"/> and is materialized by
/// <see cref="PhiTextScrubber"/> (in the leaf <c>SuavoAgent.Diagnostics</c> project, so both
/// scrub surfaces draw from one catalog and can never drift). The static API and the
/// <c>SuavoAgent.Core.Learning</c> namespace are preserved so the ~24 existing call sites
/// compile unchanged.</para>
/// </summary>
public static class PhiScrubber
{
    /// <summary>Scrub PHI from <paramref name="text"/>. Null in → null out.</summary>
    public static string? ScrubText(string? text) => PhiTextScrubber.ScrubText(text);

    /// <summary>
    /// High-recall PHI-present test (the EnforcedDenylist used by <c>OutboundPhiGuard</c>).
    /// </summary>
    public static bool ContainsPhi(string text) => PhiTextScrubber.ContainsPhi(text);

    /// <summary>
    /// ShadowDenylist scan: NAME of the first untrusted-origin (Diagnostics) rule that WOULD
    /// redact <paramref name="text"/>, or null. Shadow-mode measurement only — never enforced
    /// here. The rule name is operational metadata, never PHI.
    /// </summary>
    public static string? ShadowDenylistMatch(string text) => PhiTextScrubber.ShadowDenylistMatch(text);

    /// <summary>HMAC-SHA256 hash with per-pharmacy salt. Not dictionary-attackable.</summary>
    public static string HmacHash(string value, string salt) => PhiTextScrubber.HmacHash(value, salt);
}
