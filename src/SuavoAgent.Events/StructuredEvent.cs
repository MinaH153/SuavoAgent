using System.Text.Json.Serialization;

namespace SuavoAgent.Events;

/// <summary>
/// An audit-chain event as emitted by the agent. Cloud API ingests this shape
/// directly. Schema matches <c>docs/self-healing/audit-schema.md §Event shape</c>.
/// </summary>
/// <remarks>
/// <c>Hash</c> and <c>PrevHash</c> are set by cloud ingest, not the agent,
/// because only cloud has authoritative sequence ordering per pharmacy.
/// Agent sets everything else and trusts cloud for chain linkage.
/// </remarks>
public sealed record StructuredEvent
{
    /// <summary>UUID v7 generated at emission time (time-sortable).</summary>
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    /// <summary>Salted pharmacy identifier (see invariants.md §I.1.3).</summary>
    [JsonPropertyName("pharmacy_id")]
    public required string PharmacyId { get; init; }

    /// <summary>Canonical event type from <see cref="EventType"/>.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("category")]
    public required EventCategory Category { get; init; }

    [JsonPropertyName("severity")]
    public required EventSeverity Severity { get; init; }

    [JsonPropertyName("actor_type")]
    public required ActorType ActorType { get; init; }

    /// <summary>Agent key ID for agent-emitted events; operator UUID otherwise.</summary>
    [JsonPropertyName("actor_id")]
    public required string ActorId { get; init; }

    /// <summary>In-force Mission Charter version at emission time.</summary>
    [JsonPropertyName("mission_charter_version")]
    public required string MissionCharterVersion { get; init; }

    /// <summary>Event-type-specific payload. MUST pass PHI redaction before emission.</summary>
    [JsonPropertyName("payload")]
    public required IReadOnlyDictionary<string, object?> Payload { get; init; }

    /// <summary>Version of the redaction ruleset applied to <see cref="Payload"/>.</summary>
    [JsonPropertyName("redaction_ruleset_version")]
    public required string RedactionRulesetVersion { get; init; }

    /// <summary>Groups related events (e.g., one verb invocation).</summary>
    [JsonPropertyName("correlation_id")]
    public Guid? CorrelationId { get; init; }

    /// <summary>For child-of relationships (e.g., rollback of X).</summary>
    [JsonPropertyName("parent_id")]
    public Guid? ParentId { get; init; }

    /// <summary>When the underlying event actually happened on the agent (UTC).</summary>
    [JsonPropertyName("occurred_at")]
    public required DateTimeOffset OccurredAt { get; init; }
}
