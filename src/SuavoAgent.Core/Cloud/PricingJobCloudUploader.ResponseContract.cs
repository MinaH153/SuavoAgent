using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Cloud;

public sealed partial class PricingJobCloudUploader
{
    private static bool TryParseTerminalRejection(
        int status,
        string responseBody,
        out string code,
        out string exactResponse)
    {
        code = "";
        exactResponse = "";
        if (string.IsNullOrWhiteSpace(responseBody))
            return false;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            var names = root.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != 4 ||
                names.Distinct(StringComparer.Ordinal).Count() != 4 ||
                !names.ToHashSet(StringComparer.Ordinal).SetEquals(
                    ["accepted", "terminal", "code", "error"]) ||
                !root.TryGetProperty("accepted", out var accepted) ||
                accepted.ValueKind != JsonValueKind.False ||
                !root.TryGetProperty("terminal", out var terminal) ||
                terminal.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("code", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("error", out var errorElement) ||
                errorElement.ValueKind != JsonValueKind.String)
                return false;
            var parsedCode = codeElement.GetString() ?? "";
            var error = errorElement.GetString() ?? "";
            if (!TerminalStatuses.TryGetValue(parsedCode, out var statuses) ||
                !statuses.Contains(status) ||
                !TerminalErrors.TryGetValue(parsedCode, out var errors) ||
                !errors.Contains(error))
                return false;
            code = parsedCode;
            exactResponse = responseBody;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasValidVerifiedEnvelope(VerifiedCloudPostResponse response)
    {
        if (response.StatusCode is < 100 or > 599 ||
            response.Body.Length == 0 ||
            Encoding.UTF8.GetByteCount(response.Body) > 16 * 1024 ||
            response.KeyId != RemoteCommandTrust.CommandV1KeyId)
            return false;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(response.Body));
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    digest,
                    Convert.FromHexString(response.BodySha256)))
                return false;
            return Convert.FromBase64String(response.SignatureBase64).Length == 64;
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }

    private static bool TryParseSuccessReceipt(
        string responseBody,
        AgentStateDb.PricingResultOutboxEntry entry,
        string? expectedAgentInstanceId,
        string? expectedPharmacyId,
        bool requireIdempotent,
        out int recordedCount)
    {
        recordedCount = 0;
        if (expectedAgentInstanceId is null || expectedPharmacyId is null ||
            entry.CommandId is null || NormalizeUuidV4(entry.CommandId) is null)
            return false;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var receipt = document.RootElement;
            if (receipt.ValueKind != JsonValueKind.Object)
                return false;
            var names = receipt.EnumerateObject()
                .Select(property => property.Name)
                .ToArray();
            if (names.Length != 9 ||
                names.Distinct(StringComparer.Ordinal).Count() != 9 ||
                !names.ToHashSet(StringComparer.Ordinal).SetEquals(
                [
                    "schemaVersion", "kind", "accepted", "commandId",
                    "agentInstanceId", "pharmacyId", "jobId", "recorded",
                    "idempotent",
                ]))
                return false;

            return receipt.TryGetProperty("schemaVersion", out var schema) &&
                schema.ValueKind == JsonValueKind.Number &&
                schema.TryGetInt32(out var schemaVersion) && schemaVersion == 1 &&
                receipt.TryGetProperty("kind", out var kind) &&
                kind.ValueKind == JsonValueKind.String &&
                kind.GetString() == "pricing_result_receipt" &&
                receipt.TryGetProperty("accepted", out var accepted) &&
                accepted.ValueKind == JsonValueKind.True &&
                receipt.TryGetProperty("commandId", out var commandId) &&
                commandId.ValueKind == JsonValueKind.String &&
                commandId.GetString() == entry.CommandId &&
                receipt.TryGetProperty("agentInstanceId", out var agentId) &&
                agentId.ValueKind == JsonValueKind.String &&
                agentId.GetString() == expectedAgentInstanceId &&
                receipt.TryGetProperty("pharmacyId", out var pharmacyId) &&
                pharmacyId.ValueKind == JsonValueKind.String &&
                pharmacyId.GetString() == expectedPharmacyId &&
                receipt.TryGetProperty("jobId", out var jobId) &&
                jobId.ValueKind == JsonValueKind.String &&
                jobId.GetString() == entry.JobId &&
                receipt.TryGetProperty("recorded", out var recorded) &&
                recorded.ValueKind == JsonValueKind.Number &&
                recorded.TryGetInt32(out recordedCount) &&
                recordedCount == entry.ItemCount &&
                receipt.TryGetProperty("idempotent", out var idempotent) &&
                idempotent.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                (!requireIdempotent || idempotent.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? NormalizeUuidV4(string? value)
    {
        if (value is not { Length: 36 } ||
            !Guid.TryParseExact(value, "D", out var parsed))
            return null;
        var normalized = parsed.ToString("D");
        return value == normalized && normalized[14] == '4' &&
            normalized[19] is '8' or '9' or 'a' or 'b'
                ? normalized
                : null;
    }
}
