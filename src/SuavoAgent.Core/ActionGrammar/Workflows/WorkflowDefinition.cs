using System.Text.Json.Serialization;

namespace SuavoAgent.Core.ActionGrammarV1.Workflows;

/// <summary>
/// Cloud-side workflow definition record. Mirrors the JSON shape sent
/// alongside <c>run_workflow</c> in the signed command's <c>data</c>
/// envelope. The agent never re-validates the steps schema beyond what
/// the dispatcher does — but it DOES recompute the manifest signature
/// and refuses to execute on mismatch (CrowdStrike fail-closed).
/// </summary>
public sealed record WorkflowDefinitionDto(
    [property: JsonPropertyName("workflow_run_id")] string WorkflowRunId,
    [property: JsonPropertyName("workflow_id")] string WorkflowId,
    [property: JsonPropertyName("workflow_name")] string WorkflowName,
    [property: JsonPropertyName("workflow_version")] string WorkflowVersion,
    [property: JsonPropertyName("manifest_signature")] string ManifestSignature,
    [property: JsonPropertyName("dry_run")] bool DryRun,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("steps")] IReadOnlyList<WorkflowStepDto> Steps
);

public sealed record WorkflowStepDto(
    [property: JsonPropertyName("verb")] string Verb,
    [property: JsonPropertyName("verb_version")] string? VerbVersion,
    [property: JsonPropertyName("manifest_hash")] string? ManifestHash,
    [property: JsonPropertyName("params")] System.Text.Json.JsonElement Params,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("step_id")] string? StepId = null,
    [property: JsonPropertyName("condition")] WorkflowConditionDto? Condition = null,
    [property: JsonPropertyName("on_success")] WorkflowControlFlowDto? OnSuccess = null,
    [property: JsonPropertyName("on_failure")] WorkflowControlFlowDto? OnFailure = null
);

/// <summary>
/// Predicate evaluated BEFORE a step runs. The agent skips the step (and
/// records a synthetic "skipped" audit row) when the predicate is false.
///
/// Phase-5.4 supports a deliberately tiny grammar — the cloud renders the
/// composable shape from a workflow editor that knows the exact set of
/// expressions a workflow author can pick. Free-form expressions are out
/// of scope (see invariants.md §I.7 — typed-only, no shell, no eval).
///
/// Supported `kind` values:
///   - "previous_outcome"   — eq one of: "success" | "rejected" | "failed" | "skipped"
///   - "step_outcome"       — refers to a step by step_id; eq the same set
///   - "step_output"        — referenced step's output equals literal
///   - "always" / "never"   — escape hatches for tooling
/// </summary>
public sealed record WorkflowConditionDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("step_id")] string? StepId,
    [property: JsonPropertyName("output_key")] string? OutputKey,
    [property: JsonPropertyName("equals")] string? EqualsValue
);

/// <summary>
/// Control-flow directive evaluated after a step finishes. Three modes:
///
///   - <c>action: "continue"</c> — fall through to the next step (default).
///   - <c>action: "goto"</c> — jump to <c>goto_step_id</c>. The executor
///     refuses jumps that would form a cycle (each step may be entered at
///     most <c>max_revisits</c> times in one run, default 1).
///   - <c>action: "end"</c> — terminate the run with the provided
///     outcome (completed/failed/aborted) and reason.
///
/// On-failure-only directive: <c>action: "retry"</c> retries the same step
/// up to <c>retry_limit</c> times before falling through to the literal
/// "failed" terminal.
/// </summary>
public sealed record WorkflowControlFlowDto(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("goto_step_id")] string? GotoStepId,
    [property: JsonPropertyName("retry_limit")] int? RetryLimit,
    [property: JsonPropertyName("end_outcome")] string? EndOutcome,
    [property: JsonPropertyName("end_reason")] string? EndReason
);
