using System;

namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Audit payload emitted when a meta-roadmap wave gate fails to trip on an
/// attempt. Mirrors <c>wave.gate_failed</c> in
/// <c>docs/self-healing/event-registry.md</c>. Persisted to the
/// <c>roadmap_gates</c> table with <c>status='reset'</c> when this is a
/// counter-resetting failure.
///
/// Field semantics (see meta-roadmap §7 wave-fail recovery):
///   <list type="bullet">
///     <item><c>WaveId</c> — "W0", "W1", ..., "MASTER".</item>
///     <item><c>AttemptNumber</c> — 1, 2, 3, ... per wave.</item>
///     <item><c>FailureSummary</c> — one-paragraph diagnosis.</item>
///     <item><c>RootCauseClass</c> — one of:
///       "code-bug" | "scope-error" | "blocker-external" |
///       "architectural-error" | "pilot-crash-midsoak".</item>
///     <item><c>RemediationPlanCommittedAt</c> — null if no plan yet (e.g.,
///       blocked on external dependency).</item>
///     <item><c>NextAttemptEstimated</c> — "unknown" |
///       "when-blocker-clears" | "after-fix".</item>
///   </list>
/// </summary>
public sealed record WaveGateFailedPayload(
    string WaveId,
    int AttemptNumber,
    string FailureSummary,
    string RootCauseClass,
    DateTimeOffset? RemediationPlanCommittedAt,
    string NextAttemptEstimated);
