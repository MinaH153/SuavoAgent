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
    [property: JsonPropertyName("description")] string? Description
);
