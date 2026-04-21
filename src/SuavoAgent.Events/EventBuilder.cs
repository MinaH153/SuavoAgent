using SuavoAgent.Core.Intelligence;

namespace SuavoAgent.Events;

/// <summary>
/// Fluent builder for structured events. Enforces type validation,
/// redaction, and attribution requirements up-front so nothing leaves
/// the agent with a missing field.
/// </summary>
public sealed class EventBuilder
{
    private readonly string _agentKeyId;
    private readonly string _pharmacyId;
    private readonly string _missionCharterVersion;
    private readonly string _redactionRulesetVersion;

    public EventBuilder(
        string agentKeyId,
        string pharmacyId,
        string missionCharterVersion,
        string redactionRulesetVersion)
    {
        _agentKeyId = agentKeyId ?? throw new ArgumentNullException(nameof(agentKeyId));
        _pharmacyId = pharmacyId ?? throw new ArgumentNullException(nameof(pharmacyId));
        _missionCharterVersion = missionCharterVersion ?? throw new ArgumentNullException(nameof(missionCharterVersion));
        _redactionRulesetVersion = redactionRulesetVersion ?? throw new ArgumentNullException(nameof(redactionRulesetVersion));
    }

    /// <summary>
    /// Build a structured event, validating the type + running PHI redaction
    /// over the payload. Throws <see cref="UnknownEventTypeException"/> for
    /// unregistered types and <see cref="PhiRedactionViolationException"/>
    /// if the payload contains unredacted PHI.
    /// </summary>
    public StructuredEvent Build(
        string type,
        EventCategory category,
        EventSeverity severity,
        IReadOnlyDictionary<string, object?> payload,
        Guid? correlationId = null,
        Guid? parentId = null,
        ActorType actorType = ActorType.Agent,
        DateTimeOffset? occurredAt = null)
    {
        if (!EventType.IsKnown(type))
            throw new UnknownEventTypeException(type);

        var effectivePayload = payload ?? new Dictionary<string, object?>();

        // Run the payload through the compliance boundary before the event
        // ever touches disk or network. See invariants.md §I.1.1.
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(effectivePayload);
        var (isClean, violations) = ComplianceBoundary.Validate(payloadJson);
        if (!isClean)
            throw new PhiRedactionViolationException(type, violations);

        return new StructuredEvent
        {
            Id = Guid.NewGuid(),
            PharmacyId = _pharmacyId,
            Type = type,
            Category = category,
            Severity = severity,
            ActorType = actorType,
            ActorId = _agentKeyId,
            MissionCharterVersion = _missionCharterVersion,
            Payload = effectivePayload,
            RedactionRulesetVersion = _redactionRulesetVersion,
            CorrelationId = correlationId,
            ParentId = parentId,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow
        };
    }

    // Convenience helpers for common event shapes.

    public StructuredEvent AgentStarted(string version, IReadOnlyList<string> services, int processId) =>
        Build(EventType.AgentStarted, EventCategory.Runtime, EventSeverity.Info,
            new Dictionary<string, object?>
            {
                ["version"] = version,
                ["services"] = services,
                ["process_id"] = processId,
                ["uptime_ms"] = 0
            });

    public StructuredEvent HeartbeatEmitted(double cpuPct, long memoryMb, IReadOnlyList<string> servicesRunning) =>
        Build(EventType.HeartbeatEmitted, EventCategory.Runtime, EventSeverity.Info,
            new Dictionary<string, object?>
            {
                ["cpu_pct"] = cpuPct,
                ["memory_mb"] = memoryMb,
                ["services_running"] = servicesRunning
            });

    public StructuredEvent ServiceRestarted(string serviceName, string reason, string finalState) =>
        Build(EventType.ServiceRestarted, EventCategory.Remediation, EventSeverity.Info,
            new Dictionary<string, object?>
            {
                ["service_name"] = serviceName,
                ["reason"] = reason,
                ["final_state"] = finalState
            });

    public StructuredEvent ServiceFailed(string serviceName, int exitCode, string lastError, int consecutiveFailures) =>
        Build(EventType.ServiceFailed, EventCategory.Runtime, EventSeverity.Error,
            new Dictionary<string, object?>
            {
                ["service_name"] = serviceName,
                ["exit_code"] = exitCode,
                ["last_error"] = lastError,
                ["consecutive_failures"] = consecutiveFailures
            });

    public StructuredEvent AttestationVerified(string manifestVersion, int fileCount, long verifyDurationMs) =>
        Build(EventType.AttestationVerified, EventCategory.Security, EventSeverity.Info,
            new Dictionary<string, object?>
            {
                ["manifest_version"] = manifestVersion,
                ["file_count"] = fileCount,
                ["verify_duration_ms"] = verifyDurationMs
            });

    public StructuredEvent AttestationMismatch(string expectedManifest, IReadOnlyList<string> mismatchedFiles) =>
        Build(EventType.AttestationMismatch, EventCategory.Security, EventSeverity.Critical,
            new Dictionary<string, object?>
            {
                ["expected_manifest"] = expectedManifest,
                ["mismatched_files"] = mismatchedFiles,
                ["operator_alerted"] = true
            });
}

public sealed class UnknownEventTypeException(string type)
    : InvalidOperationException($"Unknown event type: '{type}' — register in EventType + event-registry.md");

public sealed class PhiRedactionViolationException(string eventType, IReadOnlyList<string> violations)
    : InvalidOperationException(
        $"Event '{eventType}' has PHI redaction violations: {string.Join("; ", violations)}. " +
        $"Per invariants.md §I.1, outbound events may NEVER contain unredacted PHI.");
