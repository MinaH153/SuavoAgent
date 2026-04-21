namespace SuavoAgent.Verbs;

/// <summary>
/// Expected damage envelope of a verb. Consumed by Cedar policy engine +
/// fleet operator autonomy ladder + blast-radius economics engine
/// (Codex creative idea #8). Per
/// <c>docs/self-healing/action-grammar-v1.md §Blast radius declaration</c>.
/// </summary>
/// <param name="ExpectedDollarsImpact">Cost if this verb goes wrong. 0 for safe verbs.</param>
/// <param name="PhiRecordsExposed">Upper bound on PHI records potentially exposed on rollback failure.</param>
/// <param name="DowntimeSeconds">Expected pharmacy downtime from this verb (steady state).</param>
/// <param name="RecoverableWithinSeconds">Time to roll back on detection of failure.</param>
/// <param name="Justification">Human-readable reasoning for these numbers.</param>
public sealed record VerbBlastRadius(
    decimal ExpectedDollarsImpact,
    int PhiRecordsExposed,
    int DowntimeSeconds,
    int RecoverableWithinSeconds,
    string Justification);
