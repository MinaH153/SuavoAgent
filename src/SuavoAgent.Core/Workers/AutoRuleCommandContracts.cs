using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

internal sealed record AutoRuleTransitionCommand(
    int SchemaVersion,
    string ApprovalId,
    string RuleId,
    string TemplateId,
    string YamlSha256,
    AgentStateDb.AutoRuleStatus FromStatus,
    AgentStateDb.AutoRuleStatus ToStatus,
    string? ApprovedBy,
    string? ApprovedAt,
    string ReasonCode,
    string CommandId)
{
    internal string PayloadDigest => AutoRuleCommandContracts.Digest(
        SchemaVersion.ToString(CultureInfo.InvariantCulture), ApprovalId, RuleId, TemplateId,
        YamlSha256, FromStatus.ToString(), ToStatus.ToString(), ApprovedBy, ApprovedAt,
        ReasonCode, CommandId);
}

internal sealed record AutoRuleRunCommand(
    int SchemaVersion,
    string ApprovalId,
    string RuleId,
    string TemplateId,
    string YamlSha256,
    string RunId,
    int DeadlineSeconds,
    string CommandId)
{
    internal string PayloadDigest => AutoRuleCommandContracts.Digest(
        SchemaVersion.ToString(CultureInfo.InvariantCulture), ApprovalId, RuleId, TemplateId,
        YamlSha256, RunId, DeadlineSeconds.ToString(CultureInfo.InvariantCulture), CommandId);
}

/// <summary>
/// Closed, PHI-free schemas for the learned-rule control loop. The stable command id is part of
/// signed <c>data</c>; the envelope nonce is deliberately excluded because every cloud redelivery
/// receives a fresh nonce.
/// </summary>
internal static class AutoRuleCommandContracts
{
    private static readonly HashSet<string> TransitionFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "approvalId", "ruleId", "templateId", "yamlSha256",
        "fromStatus", "toStatus", "approvedBy", "approvedAt", "reasonCode", "commandId",
    };

    private static readonly HashSet<string> RunFields = new(StringComparer.Ordinal)
    {
        "schemaVersion", "approvalId", "ruleId", "templateId", "yamlSha256",
        "runId", "deadlineSeconds", "commandId",
    };

    private static readonly Regex RuleIdPattern = new(
        @"^auto\.[a-z0-9][a-z0-9._-]{0,79}\.[a-f0-9]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimestampPattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool TryParseTransition(
        JsonElement data,
        out AutoRuleTransitionCommand? command,
        out string rejectionCode)
    {
        command = null;
        rejectionCode = "auto_rule_transition_schema_invalid";
        if (data.ValueKind != JsonValueKind.Object || !HasExactProperties(data, TransitionFields) ||
            !TrySchemaVersion(data, out var schemaVersion) ||
            !TryUuid(data, "approvalId", out var approvalId) ||
            !TryString(data, "ruleId", out var ruleId) || !RuleIdPattern.IsMatch(ruleId) ||
            !TryString(data, "templateId", out var templateId) || !IsLowerSha256(templateId) ||
            !TryString(data, "yamlSha256", out var yamlSha256) || !IsLowerSha256(yamlSha256) ||
            !TryStatus(data, "fromStatus", out var fromStatus) ||
            !TryStatus(data, "toStatus", out var toStatus) ||
            !TryNullableUuid(data, "approvedBy", out var approvedBy) ||
            !TryNullableTimestamp(data, "approvedAt", out var approvedAt) ||
            !TryString(data, "reasonCode", out var reasonCode) ||
            !TryUuid(data, "commandId", out var commandId) ||
            !IsLegalTransition(fromStatus, toStatus) ||
            !HasValidApprovalMetadata(toStatus, approvedBy, approvedAt) ||
            !string.Equals(reasonCode, ReasonFor(toStatus), StringComparison.Ordinal))
        {
            return false;
        }

        command = new AutoRuleTransitionCommand(
            schemaVersion, approvalId, ruleId, templateId, yamlSha256,
            fromStatus, toStatus, approvedBy, approvedAt, reasonCode, commandId);
        rejectionCode = "";
        return true;
    }

    internal static bool TryParseRun(
        JsonElement data,
        out AutoRuleRunCommand? command,
        out string rejectionCode)
    {
        command = null;
        rejectionCode = "auto_rule_run_schema_invalid";
        if (data.ValueKind != JsonValueKind.Object || !HasExactProperties(data, RunFields) ||
            !TrySchemaVersion(data, out var schemaVersion) ||
            !TryUuid(data, "approvalId", out var approvalId) ||
            !TryString(data, "ruleId", out var ruleId) || !RuleIdPattern.IsMatch(ruleId) ||
            !TryString(data, "templateId", out var templateId) || !IsLowerSha256(templateId) ||
            !TryString(data, "yamlSha256", out var yamlSha256) || !IsLowerSha256(yamlSha256) ||
            !TryUuid(data, "runId", out var runId) ||
            !data.TryGetProperty("deadlineSeconds", out var deadline) ||
            deadline.ValueKind != JsonValueKind.Number ||
            !deadline.TryGetInt32(out var deadlineSeconds) || deadlineSeconds is < 30 or > 900 ||
            !TryUuid(data, "commandId", out var commandId))
        {
            return false;
        }

        command = new AutoRuleRunCommand(
            schemaVersion, approvalId, ruleId, templateId, yamlSha256,
            runId, deadlineSeconds, commandId);
        rejectionCode = "";
        return true;
    }

    internal static string? TryGetCommandId(JsonElement data)
        => TryUuid(data, "commandId", out var commandId) ? commandId : null;

    internal static bool IsLegalTransition(
        AgentStateDb.AutoRuleStatus from,
        AgentStateDb.AutoRuleStatus to) => (from, to) switch
        {
            (AgentStateDb.AutoRuleStatus.Pending, AgentStateDb.AutoRuleStatus.Shadow) => true,
            (AgentStateDb.AutoRuleStatus.Pending, AgentStateDb.AutoRuleStatus.Rejected) => true,
            (AgentStateDb.AutoRuleStatus.Shadow, AgentStateDb.AutoRuleStatus.Approved) => true,
            (AgentStateDb.AutoRuleStatus.Shadow, AgentStateDb.AutoRuleStatus.Rejected) => true,
            (AgentStateDb.AutoRuleStatus.Shadow, AgentStateDb.AutoRuleStatus.Pending) => true,
            (AgentStateDb.AutoRuleStatus.Approved, AgentStateDb.AutoRuleStatus.Rejected) => true,
            (AgentStateDb.AutoRuleStatus.Rejected, AgentStateDb.AutoRuleStatus.Pending) => true,
            _ => false,
        };

    internal static string Digest(params string?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var field in fields)
        {
            if (field is null)
            {
                hash.AppendData(new byte[] { 0xff, 0xff, 0xff, 0xff });
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(field);
            hash.AppendData(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length)));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool HasExactProperties(JsonElement data, HashSet<string> expected)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (!names.Add(property.Name)) return false;
        return names.SetEquals(expected);
    }

    private static bool TrySchemaVersion(JsonElement data, out int version)
    {
        version = 0;
        return data.TryGetProperty("schemaVersion", out var element) &&
               element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out version) && version == 1;
    }

    private static bool TryStatus(
        JsonElement data,
        string name,
        out AgentStateDb.AutoRuleStatus status)
    {
        status = default;
        return TryString(data, name, out var value) &&
               Enum.TryParse(value, ignoreCase: false, out status) &&
               string.Equals(status.ToString(), value, StringComparison.Ordinal);
    }

    private static bool TryUuid(JsonElement data, string name, out string value)
        => TryString(data, name, out value) && IsCanonicalUuid(value);

    private static bool TryNullableUuid(JsonElement data, string name, out string? value)
    {
        value = null;
        if (!data.TryGetProperty(name, out var element)) return false;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        var candidate = element.GetString() ?? "";
        if (!IsCanonicalUuid(candidate)) return false;
        value = candidate;
        return true;
    }

    private static bool TryNullableTimestamp(JsonElement data, string name, out string? value)
    {
        value = null;
        if (!data.TryGetProperty(name, out var element)) return false;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        var candidate = element.GetString() ?? "";
        if (!TimestampPattern.IsMatch(candidate) ||
            !DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            return false;
        value = candidate;
        return true;
    }

    private static bool TryString(JsonElement data, string name, out string value)
    {
        value = "";
        if (!data.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? "";
        return value.Length is > 0 and <= 200 && !value.Any(char.IsControl);
    }

    private static bool IsCanonicalUuid(string value) =>
        value.Length == 36 && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasValidApprovalMetadata(
        AgentStateDb.AutoRuleStatus to,
        string? approvedBy,
        string? approvedAt) =>
        to == AgentStateDb.AutoRuleStatus.Approved
            ? approvedBy is not null && approvedAt is not null
            : approvedBy is null && approvedAt is null;

    private static string ReasonFor(AgentStateDb.AutoRuleStatus to) => to switch
    {
        AgentStateDb.AutoRuleStatus.Approved => "human_approved",
        AgentStateDb.AutoRuleStatus.Rejected => "operator_rejected",
        AgentStateDb.AutoRuleStatus.Shadow => "shadow_started",
        _ => "operator_reset",
    };
}
