namespace SuavoAgent.Core.Mission;

/// <summary>
/// Mission charter v0.1 skeleton — per-pharmacy contract that defines what the
/// Mission Loop is optimising for, what it must never cross, and how it
/// prioritises conflicting objectives.
///
/// Scaffolding only. Not wired into host startup. Code paths that consume
/// the charter will land post-Nadim pilot (Saturday 2026-04-25) once
/// `docs/self-healing/phase-a-architecture.md` items A1/A2 close.
///
/// See:
///   docs/self-healing/invariants.md
///   docs/self-healing/action-grammar-v1.md
///   .claude/projects/-Users-joshuahenein/memory/suavoagent-mission-loop-architecture.md
/// </summary>
public sealed record MissionCharter(
    Guid CharterId,
    string PharmacyId,
    int Version,
    DateTimeOffset EffectiveFrom,
    IReadOnlyList<MissionObjective> Objectives,
    IReadOnlyList<MissionConstraint> Constraints,
    MissionPriorityOrdering PriorityOrdering,
    MissionToleranceThresholds Tolerance,
    string SignedByOperator,
    DateTimeOffset SignedAt
);

public sealed record MissionObjective(string Id, string Description, int Weight);

public sealed record MissionConstraint(string Id, string Kind, string Expression, string Justification);

public sealed record MissionPriorityOrdering(IReadOnlyList<string> OrderedObjectiveIds);

public sealed record MissionToleranceThresholds(
    int MaxDowntimeSecondsPerShift,
    int MaxRetriesBeforeEscalation,
    double MinCacheHitRateForAutonomy
);
