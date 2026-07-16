using System.Collections.Frozen;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Core.State;
using SuavoAgent.Diagnostics;

namespace SuavoAgent.Core.Cloud;

public sealed partial class WorkflowAuditCloudClient
{
    private static readonly Regex IsoTimestampPattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private bool TryParseAuditReceipt(
        VerifiedCloudPostResponse response,
        AgentStateDb.WorkflowAuditEventOutboxEntry entry,
        out string receiptDigest)
    {
        receiptDigest = "";
        if (response.StatusCode != 200) return false;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (!HasExactKeys(root,
                    "schemaVersion", "kind", "workflowRunId",
                    "agentInstanceId", "pharmacyId", "eventId",
                    "executionOrdinal", "auditId", "receiptDigest",
                    "idempotent") ||
                !TryInt(root, "schemaVersion", out var schema) || schema != 1 ||
                !TryString(root, "kind", out var kind) ||
                    kind != "workflow_audit_receipt" ||
                !TryString(root, "workflowRunId", out var runId) ||
                    runId != entry.WorkflowRunId ||
                !TryString(root, "agentInstanceId", out var agentId) ||
                    agentId != _expectedAgentId ||
                !TryString(root, "pharmacyId", out var pharmacyId) ||
                    pharmacyId != _expectedPharmacyId ||
                !TryString(root, "eventId", out var eventId) ||
                    eventId != entry.EventId.ToString("D") ||
                !TryInt(root, "executionOrdinal", out var ordinal) ||
                    ordinal != entry.ExecutionOrdinal ||
                !TryString(root, "auditId", out var auditId) ||
                    !Guid.TryParseExact(auditId, "D", out _) ||
                !TryString(root, "receiptDigest", out var digest) ||
                    !IsLowerHexSha256(digest) ||
                    digest != ComputeAuditReceiptDigest(
                        entry.WorkflowRunId,
                        _expectedAgentId,
                        _expectedPharmacyId,
                        entry.EventId,
                        entry.ExecutionOrdinal,
                        entry.StepIndex,
                        entry.VerbName,
                        entry.VerbVersion,
                        entry.RequestedDryRun,
                        entry.EffectiveDryRun,
                        entry.Outcome,
                        entry.ExecDurationMs,
                        entry.ErrorKind,
                        entry.ParamsFieldCount,
                        entry.BeforeStateFieldCount,
                        entry.AfterStateFieldCount) ||
                !root.TryGetProperty("idempotent", out var idempotent) ||
                    idempotent.ValueKind is not (
                        JsonValueKind.True or JsonValueKind.False))
                return false;
            receiptDigest = digest;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string ComputeAuditReceiptDigest(
        string workflowRunId,
        string agentInstanceId,
        string pharmacyId,
        Guid eventId,
        int executionOrdinal,
        int stepIndex,
        string verbName,
        string verbVersion,
        bool requestedDryRun,
        bool? effectiveDryRun,
        string outcome,
        int? execDurationMs,
        string? errorKind,
        int paramsFieldCount,
        int? beforeStateFieldCount,
        int? afterStateFieldCount)
    {
        var logicalReceipt = new SortedDictionary<string, object?>(
            StringComparer.Ordinal)
        {
            ["afterStateFieldCount"] = afterStateFieldCount,
            ["agentInstanceId"] = agentInstanceId,
            ["beforeStateFieldCount"] = beforeStateFieldCount,
            ["effectiveDryRun"] = effectiveDryRun,
            ["errorKind"] = errorKind,
            ["eventId"] = eventId.ToString("D"),
            ["execDurationMs"] = execDurationMs,
            ["executionOrdinal"] = executionOrdinal,
            ["outcome"] = outcome,
            ["paramsFieldCount"] = paramsFieldCount,
            ["pharmacyId"] = pharmacyId,
            ["requestedDryRun"] = requestedDryRun,
            ["schemaVersion"] = 1,
            ["stepIndex"] = stepIndex,
            ["verbName"] = verbName,
            ["verbVersion"] = verbVersion,
            ["workflowRunId"] = workflowRunId,
        };
        return ComputeCanonicalDigest(logicalReceipt);
    }

    private bool TryParseCompletionReceipt(
        VerifiedCloudPostResponse response,
        AgentStateDb.WorkflowCompletionOutboxEntry entry,
        out string completionReceiptDigest)
    {
        completionReceiptDigest = "";
        if (response.StatusCode != 200) return false;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (!HasExactKeys(root,
                    "schemaVersion", "kind", "workflowRunId",
                    "agentInstanceId", "pharmacyId", "completionId",
                    "auditEventCount", "finalEventId", "auditChainDigest",
                    "completionReceiptDigest", "status", "reasonCode",
                    "completedAt", "abortedAt", "idempotent") ||
                !TryInt(root, "schemaVersion", out var schema) || schema != 1 ||
                !TryString(root, "kind", out var kind) ||
                    kind != "workflow_completion_receipt" ||
                !TryString(root, "workflowRunId", out var runId) ||
                    runId != entry.WorkflowRunId ||
                !TryString(root, "agentInstanceId", out var agentId) ||
                    agentId != _expectedAgentId ||
                !TryString(root, "pharmacyId", out var pharmacyId) ||
                    pharmacyId != _expectedPharmacyId ||
                !TryString(root, "completionId", out var completionId) ||
                    completionId != entry.CompletionId.ToString("D") ||
                !TryInt(root, "auditEventCount", out var count) ||
                    count != entry.AuditEventCount ||
                !MatchesNullableString(
                    root, "finalEventId", entry.FinalEventId?.ToString("D")) ||
                !TryString(root, "auditChainDigest", out var chain) ||
                    chain != entry.AuditChainDigest ||
                !TryString(
                    root, "completionReceiptDigest", out var receiptDigest) ||
                    !IsLowerHexSha256(receiptDigest) ||
                    receiptDigest != ComputeCompletionReceiptDigest(
                        entry.WorkflowRunId,
                        _expectedAgentId,
                        _expectedPharmacyId,
                        entry.CompletionId,
                        entry.AuditEventCount,
                        entry.FinalEventId,
                        entry.AuditChainDigest,
                        entry.Outcome,
                        entry.ReasonCode) ||
                !TryString(root, "status", out var status) ||
                    status != entry.Outcome ||
                !MatchesNullableString(root, "reasonCode", entry.ReasonCode) ||
                !HasExactTerminalTimestamps(root, entry.Outcome) ||
                !root.TryGetProperty("idempotent", out var idempotent) ||
                    idempotent.ValueKind is not (
                        JsonValueKind.True or JsonValueKind.False))
                return false;
            completionReceiptDigest = receiptDigest;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string ComputeCompletionReceiptDigest(
        string workflowRunId,
        string agentInstanceId,
        string pharmacyId,
        Guid completionId,
        int auditEventCount,
        Guid? finalEventId,
        string auditChainDigest,
        string outcome,
        string? reasonCode)
    {
        var logicalReceipt = new SortedDictionary<string, object?>(
            StringComparer.Ordinal)
        {
            ["agentInstanceId"] = agentInstanceId,
            ["auditChainDigest"] = auditChainDigest,
            ["auditEventCount"] = auditEventCount,
            ["completionId"] = completionId.ToString("D"),
            ["finalEventId"] = finalEventId?.ToString("D"),
            ["outcome"] = outcome,
            ["pharmacyId"] = pharmacyId,
            ["reasonCode"] = reasonCode,
            ["schemaVersion"] = 1,
            ["workflowRunId"] = workflowRunId,
        };
        return ComputeCanonicalDigest(logicalReceipt);
    }

    private static string ComputeCanonicalDigest(
        IReadOnlyDictionary<string, object?> logicalReceipt)
    {
        var canonical = Rfc8785Canonicalizer.CanonicalizeToUtf8(
            JsonSerializer.Serialize(logicalReceipt));
        try
        {
            return Convert.ToHexString(SHA256.HashData(canonical))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static bool TryParseRejection(
        VerifiedCloudPostResponse response,
        string expectedKind,
        FrozenDictionary<int, FrozenSet<string>> terminalCodes,
        string retryableCode,
        out bool terminal,
        out string code)
    {
        terminal = false;
        code = "";
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (!HasExactKeys(
                    root, "schemaVersion", "kind", "accepted", "terminal", "code") ||
                !TryInt(root, "schemaVersion", out var schema) || schema != 1 ||
                !TryString(root, "kind", out var kind) || kind != expectedKind ||
                !root.TryGetProperty("accepted", out var accepted) ||
                    accepted.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("terminal", out var terminalElement) ||
                    terminalElement.ValueKind is not (
                        JsonValueKind.True or JsonValueKind.False) ||
                !TryString(root, "code", out code))
                return false;
            terminal = terminalElement.GetBoolean();
            if (!terminal)
                return response.StatusCode == 503 && code == retryableCode;
            return terminalCodes.TryGetValue(response.StatusCode, out var codes) &&
                codes.Contains(code);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidatePersistedAuditPayload(
        AgentStateDb.WorkflowAuditEventOutboxEntry entry)
    {
        if (Sha256(entry.PayloadJson) != entry.PayloadSha256) return false;
        try
        {
            using var document = JsonDocument.Parse(entry.PayloadJson);
            var root = document.RootElement;
            return HasExactKeys(root,
                    "schemaVersion", "eventId", "executionOrdinal", "stepIndex",
                    "verbName", "verbVersion", "requestedDryRun",
                    "effectiveDryRun", "outcome", "execDurationMs", "errorKind",
                    "paramsFieldCount", "beforeStateFieldCount",
                    "afterStateFieldCount") &&
                TryInt(root, "schemaVersion", out var schema) && schema == 1 &&
                TryString(root, "eventId", out var eventId) &&
                    eventId == entry.EventId.ToString("D") &&
                TryInt(root, "executionOrdinal", out var ordinal) &&
                    ordinal == entry.ExecutionOrdinal &&
                TryInt(root, "stepIndex", out var step) && step == entry.StepIndex &&
                TryString(root, "verbName", out var verb) && verb == entry.VerbName &&
                TryString(root, "verbVersion", out var version) &&
                    version == entry.VerbVersion &&
                TryBool(root, "requestedDryRun", out var requested) &&
                    requested == entry.RequestedDryRun &&
                MatchesNullableBool(root, "effectiveDryRun", entry.EffectiveDryRun) &&
                TryString(root, "outcome", out var outcome) &&
                    outcome == entry.Outcome &&
                MatchesNullableInt(root, "execDurationMs", entry.ExecDurationMs) &&
                MatchesNullableString(root, "errorKind", entry.ErrorKind) &&
                TryInt(root, "paramsFieldCount", out var paramsCount) &&
                    paramsCount == entry.ParamsFieldCount &&
                MatchesNullableInt(
                    root, "beforeStateFieldCount", entry.BeforeStateFieldCount) &&
                MatchesNullableInt(
                    root, "afterStateFieldCount", entry.AfterStateFieldCount);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidatePersistedCompletionPayload(
        AgentStateDb.WorkflowCompletionOutboxEntry entry)
    {
        if (Sha256(entry.PayloadJson) != entry.PayloadSha256) return false;
        try
        {
            using var document = JsonDocument.Parse(entry.PayloadJson);
            var root = document.RootElement;
            return HasExactKeys(root,
                    "schemaVersion", "completionId", "outcome", "reasonCode",
                    "auditEventCount", "finalEventId", "auditChainDigest") &&
                TryInt(root, "schemaVersion", out var schema) && schema == 1 &&
                TryString(root, "completionId", out var completionId) &&
                    completionId == entry.CompletionId.ToString("D") &&
                TryString(root, "outcome", out var outcome) &&
                    outcome == entry.Outcome &&
                MatchesNullableString(root, "reasonCode", entry.ReasonCode) &&
                TryInt(root, "auditEventCount", out var count) &&
                    count == entry.AuditEventCount &&
                MatchesNullableString(
                    root, "finalEventId", entry.FinalEventId?.ToString("D")) &&
                TryString(root, "auditChainDigest", out var chain) &&
                    chain == entry.AuditChainDigest;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactKeys(JsonElement root, params string[] expected)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length &&
            names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            names.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool TryString(
        JsonElement root,
        string name,
        out string value)
    {
        value = "";
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? "";
        return true;
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out value);
    }

    private static bool TryBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool MatchesNullableString(
        JsonElement root,
        string name,
        string? expected)
    {
        if (!root.TryGetProperty(name, out var element)) return false;
        return expected is null
            ? element.ValueKind == JsonValueKind.Null
            : element.ValueKind == JsonValueKind.String &&
                element.GetString() == expected;
    }

    private static bool MatchesNullableInt(
        JsonElement root,
        string name,
        int? expected)
    {
        if (!root.TryGetProperty(name, out var element)) return false;
        return expected is null
            ? element.ValueKind == JsonValueKind.Null
            : element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out var value) && value == expected;
    }

    private static bool MatchesNullableBool(
        JsonElement root,
        string name,
        bool? expected)
    {
        if (!root.TryGetProperty(name, out var element)) return false;
        return expected is null
            ? element.ValueKind == JsonValueKind.Null
            : element.ValueKind is (JsonValueKind.True or JsonValueKind.False) &&
                element.GetBoolean() == expected;
    }

    private static bool HasExactTerminalTimestamps(
        JsonElement root,
        string outcome)
    {
        if (!root.TryGetProperty("completedAt", out var completed) ||
            !root.TryGetProperty("abortedAt", out var aborted))
            return false;
        return outcome == "aborted"
            ? completed.ValueKind == JsonValueKind.Null && IsIsoTimestamp(aborted)
            : IsIsoTimestamp(completed) && aborted.ValueKind == JsonValueKind.Null;
    }

    private static bool IsIsoTimestamp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            return false;
        var value = element.GetString();
        return value is not null &&
            IsoTimestampPattern.IsMatch(value) &&
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _);
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 &&
        value.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
