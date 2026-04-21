namespace SuavoAgent.Events;

/// <summary>
/// Taxonomy axis for event reporting. Orthogonal to <see cref="EventType"/>.
/// Defined in docs/self-healing/event-registry.md §Event categories.
/// </summary>
public enum EventCategory
{
    /// <summary>Install, upgrade, uninstall.</summary>
    Install,

    /// <summary>Ongoing agent operation.</summary>
    Runtime,

    /// <summary>L1 dispatch activity (Phase C+).</summary>
    Diagnosis,

    /// <summary>L2 verb activity (Phase D+).</summary>
    Remediation,

    /// <summary>L3 plan + consent + autonomy (Phase E+).</summary>
    Governance,

    /// <summary>Kill switches, invariant violations, key events.</summary>
    Security,

    /// <summary>BAA, HIPAA, auditor-facing.</summary>
    Compliance,

    /// <summary>Operator dashboard activity.</summary>
    Ops
}
