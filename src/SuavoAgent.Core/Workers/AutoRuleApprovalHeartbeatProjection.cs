using System.Text.Json.Serialization;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

internal sealed record AutoRuleApprovalHeartbeatItem(
    [property: JsonPropertyName("ruleId")] string RuleId,
    [property: JsonPropertyName("templateId")] string TemplateId,
    [property: JsonPropertyName("yamlSha256")] string YamlSha256,
    [property: JsonPropertyName("hasWriteback")] bool HasWriteback,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("shadowRuns")] int ShadowRuns,
    [property: JsonPropertyName("shadowMatches")] int ShadowMatches,
    [property: JsonPropertyName("shadowMismatches")] int ShadowMismatches,
    [property: JsonPropertyName("approvedBy")] string? ApprovedBy,
    [property: JsonPropertyName("approvedAt")] string? ApprovedAt,
    [property: JsonPropertyName("rejectedReason")] string? RejectedReason);

internal static class AutoRuleApprovalHeartbeatProjection
{
    internal static AutoRuleApprovalHeartbeatItem[] Project(
        IReadOnlyList<AgentStateDb.AutoRuleApprovalRow> rows) =>
        rows.Select(row => new AutoRuleApprovalHeartbeatItem(
            row.RuleId,
            row.TemplateId,
            row.YamlSha256,
            row.HasWriteback,
            row.Status.ToString(),
            row.ShadowRuns,
            row.ShadowMatches,
            row.ShadowMismatches,
            row.ApprovedBy,
            row.ApprovedAt,
            row.RejectedReason)).ToArray();
}
