using System;
using System.Collections.Generic;

namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Audit payload emitted when a meta-roadmap wave gate trips. Mirrors the
/// canonical shape declared in <c>docs/self-healing/event-registry.md</c>
/// under the <c>wave.gate_tripped</c> entry. Consumed by the cloud's
/// <c>roadmap_gates</c> table writer plus the audit chain ingest.
///
/// Field semantics (see meta-roadmap §8):
///   <list type="bullet">
///     <item><c>WaveId</c> — "W0", "W1", ..., "MASTER".</item>
///     <item><c>EvidenceSummary</c> — one-paragraph human-readable rationale.</item>
///     <item><c>CertifiedBy</c> — "ci" | "pilot:&lt;pharmacy_id_hash&gt;" | "joshua".</item>
///     <item><c>EvidenceEventIds</c> — pointers to supporting <c>audit_events</c> rows.</item>
///     <item><c>TrippedAt</c> — UTC instant the gate is considered tripped.</item>
///   </list>
/// </summary>
public sealed record WaveGateTrippedPayload(
    string WaveId,
    string EvidenceSummary,
    string CertifiedBy,
    IReadOnlyList<string> EvidenceEventIds,
    DateTimeOffset TrippedAt);
