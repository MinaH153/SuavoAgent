namespace SuavoAgent.Diagnostics;

/// <summary>
/// Immutable snapshot of the ruleset + its derived hot-path components.
/// </summary>
/// <remarks>
/// <para>
/// Codex Comp 2 chunk B HIGH RESOLVED. Phase 1 stored <c>_ruleset</c>,
/// <c>_scrubber</c>, and <c>_fingerprinter</c> as three INDEPENDENT static
/// fields on <see cref="Wire"/>. Under the .NET memory model
/// (ECMA-335 §I.12.6.6 – §I.12.6.7), a reader on a signal-emit thread can
/// observe new-ruleset + old-scrubber + old-fingerprinter mid-swap.
/// Wrapping the three references in a single immutable record lets us
/// publish a NEW generation atomically with one
/// <see cref="System.Threading.Volatile.Write{T}(ref T, T)"/> + every
/// reader takes ONE <see cref="System.Threading.Volatile.Read{T}(ref T)"/>
/// at the top of their work, never re-reads mid-dispatch.
/// </para>
/// <para>
/// Built OFF-LOCK at swap time to avoid lock-order hazards with emit paths
/// re-entering Wire. The record is immutable; <c>with</c> expressions
/// allocate a new generation rather than mutating in place.
/// </para>
/// </remarks>
public sealed record RulesetRuntime(
    RulesetV1 Ruleset,
    PhiScrubber Scrubber,
    FingerprintComputer Fingerprinter);
