namespace SuavoAgent.Core.ActionGrammarV1;

public enum VerbRiskTier
{
    Unknown = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public abstract record VerbBaaScope
{
    public sealed record None : VerbBaaScope;
    public sealed record AgentBaa : VerbBaaScope;
    public sealed record BaaAmendment(string AmendmentId) : VerbBaaScope;
    public sealed record Forbidden : VerbBaaScope;
}

public sealed record VerbBlastRadius(
    decimal ExpectedDollarsImpact,
    int PhiRecordsExposed,
    int DowntimeSeconds,
    int RecoverableWithinSeconds,
    string Justification
);

public sealed record VerbParameterSpec(
    string Name,
    Type ClrType,
    bool Required,
    string? ValidationHint = null
);

public sealed record VerbParameterSchema(IReadOnlyList<VerbParameterSpec> Parameters);

public sealed record VerbOutputSpec(string Name, Type ClrType);

public sealed record VerbOutputSchema(IReadOnlyList<VerbOutputSpec> Fields);

/// <summary>
/// Per-verb declaration consumed by the dispatcher, policy engine, audit
/// trail, and plan-review UI. Contract from
/// docs/self-healing/action-grammar-v1.md §Verb schema.
///
/// This is the Phase-1 metadata surface. Fields required for cross-process
/// dispatch (signed bundle hashes, concurrency declarations) intentionally
/// omitted — Phase 1 runs in-process against trusted local adapters.
/// Those fields land with the signed verb-bundle registry (Phase D).
/// </summary>
public sealed record VerbMetadata(
    string Name,
    string Version,
    string Description,
    VerbRiskTier RiskTier,
    VerbBaaScope BaaScope,
    bool IsMutation,
    bool IsDestructive,
    TimeSpan MaxExecutionTime,
    VerbParameterSchema Params,
    VerbOutputSchema Output,
    VerbBlastRadius BlastRadius
);
