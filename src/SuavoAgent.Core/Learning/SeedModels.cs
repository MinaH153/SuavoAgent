using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Core.Learning;

public sealed record SeedRequest(
    [property: JsonPropertyName("adapter_type")] string AdapterType,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("contract_fingerprint")] string ContractFingerprint,
    [property: JsonPropertyName("pms_version_hash")] string PmsVersionHash,
    [property: JsonPropertyName("tree_hashes")] IReadOnlyList<string> TreeHashes,
    [property: JsonPropertyName("last_seed_digest")] string? LastSeedDigest);

public sealed record SeedResponse(
    [property: JsonPropertyName("seed_digest")] string SeedDigest,
    [property: JsonPropertyName("seed_version")] int SeedVersion,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("gates_passed")] IReadOnlyList<string> GatesPassed,
    [property: JsonPropertyName("ui_overlap")] UiOverlap? UiOverlap,
    [property: JsonPropertyName("correlations")] IReadOnlyList<SeedCorrelation>? Correlations,
    [property: JsonPropertyName("query_shapes")] IReadOnlyList<SeedQueryShape> QueryShapes,
    [property: JsonPropertyName("status_mappings")] IReadOnlyList<SeedStatusMapping> StatusMappings,
    [property: JsonPropertyName("workflow_hints")] IReadOnlyList<SeedWorkflowHint>? WorkflowHints,
    [property: JsonPropertyName("workflow_templates")] IReadOnlyList<SeedWorkflowTemplate>? WorkflowTemplates = null);

// v3.12 — workflow template transfer currency (spec §6). Steps ride on the
// existing ECDSA-verified SeedResponse envelope (SeedClient uses
// PostSignedVerifiedAsync), so integrity is protected end-to-end.
public sealed record SeedWorkflowTemplate(
    [property: JsonPropertyName("template_id")] string TemplateId,
    [property: JsonPropertyName("template_version")] string TemplateVersion,
    [property: JsonPropertyName("skill_id")] string SkillId,
    [property: JsonPropertyName("process_name_glob")] string ProcessNameGlob,
    [property: JsonPropertyName("pms_version_range")] IReadOnlyList<PmsVersionFingerprint> PmsVersionRange,
    [property: JsonPropertyName("screen_signature")] string ScreenSignatureV1,
    [property: JsonPropertyName("steps_hash")] string StepsHash,
    [property: JsonPropertyName("steps")] IReadOnlyList<TemplateStep> Steps,
    [property: JsonPropertyName("aggregate_confidence")] double AggregateConfidence,
    [property: JsonPropertyName("contributor_count")] int ContributorCount,
    [property: JsonPropertyName("fleet_match_count")] int FleetMatchCount,
    [property: JsonPropertyName("fleet_mismatch_count")] int FleetMismatchCount,
    [property: JsonPropertyName("has_writeback")] bool HasWriteback);

public sealed record UiOverlap(
    [property: JsonPropertyName("matched")] int Matched,
    [property: JsonPropertyName("total_local")] int TotalLocal,
    [property: JsonPropertyName("overlap_ratio")] double OverlapRatio);

public sealed record SeedCorrelation(
    [property: JsonPropertyName("correlation_key")] string CorrelationKey,
    [property: JsonPropertyName("tree_hash")] string TreeHash,
    [property: JsonPropertyName("element_id")] string ElementId,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("query_shape_hash")] string QueryShapeHash,
    [property: JsonPropertyName("aggregate_confidence")] double AggregateConfidence,
    [property: JsonPropertyName("aggregate_success_rate")] double AggregateSuccessRate,
    [property: JsonPropertyName("contributor_count")] int ContributorCount,
    [property: JsonPropertyName("seeded_confidence")] double SeededConfidence);

public sealed record SeedQueryShape(
    [property: JsonPropertyName("query_shape_hash")] string QueryShapeHash,
    [property: JsonPropertyName("parameterized_sql")] string ParameterizedSql,
    [property: JsonPropertyName("tables_referenced")] IReadOnlyList<string> TablesReferenced,
    [property: JsonPropertyName("aggregate_confidence")] double AggregateConfidence,
    [property: JsonPropertyName("contributor_count")] int ContributorCount);

public sealed record SeedStatusMapping(
    [property: JsonPropertyName("status_table")] string StatusTable,
    [property: JsonPropertyName("status_guid")] string StatusGuid,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("contributor_count")] int ContributorCount);

public sealed record SeedWorkflowHint(
    [property: JsonPropertyName("routine_hash")] string RoutineHash,
    [property: JsonPropertyName("path_length")] int PathLength,
    [property: JsonPropertyName("avg_frequency")] double AvgFrequency,
    [property: JsonPropertyName("has_writeback_candidate")] bool HasWritebackCandidate,
    [property: JsonPropertyName("contributor_count")] int ContributorCount);

public sealed record SeedConfirmRequest(
    [property: JsonPropertyName("seed_digest")] string SeedDigest,
    [property: JsonPropertyName("applied_at")] string AppliedAt,
    [property: JsonPropertyName("correlations_applied")] int CorrelationsApplied,
    [property: JsonPropertyName("correlations_skipped")] int CorrelationsSkipped);

public sealed record GateResult(string Name, bool Passed, string Detail);
