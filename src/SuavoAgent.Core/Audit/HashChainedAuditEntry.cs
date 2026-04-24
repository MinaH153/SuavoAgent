namespace SuavoAgent.Core.Audit;

/// <summary>
/// One entry in the per-pharmacy hash-chained audit log.
/// Spec: docs/self-healing/audit-schema.md (locked 2026-04-21).
///
/// Scaffolding: in-memory chain via <see cref="AuditChain"/>. Persistence to
/// Postgres + S3 Object Lock lands in Phase A item A2 post-Nadim.
/// </summary>
public sealed record HashChainedAuditEntry(
    long SequenceNumber,
    Guid EntryId,
    DateTimeOffset OccurredAt,
    string EventType,
    string Actor,
    string SubjectType,
    string SubjectId,
    IReadOnlyDictionary<string, object?> Metadata,
    string PreviousEntryHash,
    string EntryHash
);
