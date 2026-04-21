namespace SuavoAgent.Verbs;

/// <summary>
/// Self-describing verb contract declared by every <see cref="IVerb"/>.
/// Cloud dispatcher reads this, policy engine evaluates against it,
/// audit trail records it. Per
/// <c>docs/self-healing/action-grammar-v1.md §Verb schema</c>.
/// </summary>
public sealed record VerbMetadata(
    /// <summary>e.g., "restart_service"</summary>
    string Name,

    /// <summary>Strict semver. MAJOR bump = breaking schema change.</summary>
    string Version,

    /// <summary>Human-readable description.</summary>
    string Description,

    VerbRiskTier RiskTier,

    VerbBaaScope BaaScope,

    /// <summary>true if verb changes on-box state (vs read-only diagnostic).</summary>
    bool IsMutation,

    /// <summary>true if rollback is lossy (e.g., rotate_api_key).</summary>
    bool IsDestructive,

    /// <summary>Watchdog timeout before execution is forcibly aborted.</summary>
    TimeSpan MaxExecutionTime,

    VerbParameterSchema Parameters,

    VerbOutputSchema Output,

    VerbBlastRadius BlastRadius,

    /// <summary>Other verbs that must have run successfully before this one.</summary>
    IReadOnlyList<string> RequiresVerbs,

    /// <summary>Verbs that cannot run concurrently with this one.</summary>
    IReadOnlyList<string> ConflictingVerbs);
