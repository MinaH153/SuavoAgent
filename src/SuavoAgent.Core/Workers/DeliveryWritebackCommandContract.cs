using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Strict PHI-minimal delivered-command allow-list. The database data_json has
/// thirteen business fields; heartbeat injects stable <c>commandId</c> as the
/// exact fourteenth signed data field. The envelope nonce is intentionally fresh on
/// every redelivery and is used only by replay protection.
/// </summary>
internal static class DeliveryWritebackCommandContract
{
    private static readonly HashSet<string> ExactDataFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "writebackId",
        "candidateId",
        "rxHash",
        "evidenceId",
        "pharmacyId",
        "orderId",
        "inboxItemId",
        "pmsReferenceId",
        "proofRecordId",
        "proofDigest",
        "transition",
        "transitionAt",
        "commandId",
    };

    private static readonly Regex TransitionTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool TryParse(
        JsonElement data,
        out AgentDeliveryWritebackCommand? command,
        out string rejectionCode)
    {
        command = null;
        rejectionCode = "delivery_writeback_schema_invalid";
        if (data.ValueKind != JsonValueKind.Object || !HasExactProperties(data) ||
            !data.TryGetProperty("schemaVersion", out var schemaElement) ||
            schemaElement.ValueKind != JsonValueKind.Number ||
            !schemaElement.TryGetInt32(out var schemaVersion) || schemaVersion != 2 ||
            !TryString(data, "writebackId", out var writebackId) || !IsCanonicalUuid(writebackId) ||
            !TryString(data, "candidateId", out var candidateId) || !IsCanonicalUuid(candidateId) ||
            !TryString(data, "rxHash", out var rxHash) || !IsLowerSha256(rxHash) ||
            !TryString(data, "evidenceId", out var evidenceId) || !IsEvidence(evidenceId, rxHash) ||
            !TryString(data, "pharmacyId", out var pharmacyId) || !IsCanonicalUuid(pharmacyId) ||
            !TryString(data, "orderId", out var orderId) || !IsCanonicalUuid(orderId) ||
            !TryString(data, "inboxItemId", out var inboxItemId) || !IsCanonicalUuid(inboxItemId) ||
            !TryString(data, "pmsReferenceId", out var pmsReferenceId) || !IsCanonicalUuid(pmsReferenceId) ||
            !TryNullableString(data, "proofRecordId", out var proofRecordId) ||
            !TryNullableString(data, "proofDigest", out var proofDigest) ||
            !TryString(data, "transition", out var transition) || transition is not ("pickup" or "complete") ||
            !TryString(data, "transitionAt", out var transitionAt) ||
            !TransitionTimestamp.IsMatch(transitionAt) ||
            !DateTimeOffset.TryParse(
                transitionAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _) ||
            !TryString(data, "commandId", out var commandId) || !IsCanonicalUuid(commandId))
        {
            return false;
        }

        var hasProof = proofRecordId is not null && proofDigest is not null;
        if ((transition == "complete" &&
             (!hasProof || !IsCanonicalUuid(proofRecordId!) || !IsLowerSha256(proofDigest!))) ||
            (transition == "pickup" && (proofRecordId is not null || proofDigest is not null)))
            return false;

        command = new AgentDeliveryWritebackCommand(
            schemaVersion,
            writebackId,
            candidateId,
            rxHash,
            evidenceId,
            pharmacyId,
            orderId,
            inboxItemId,
            pmsReferenceId,
            proofRecordId,
            proofDigest,
            transition,
            transitionAt,
            commandId);
        rejectionCode = "";
        return true;
    }

    private static bool HasExactProperties(JsonElement data)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (!names.Add(property.Name)) return false;
        return names.SetEquals(ExactDataFields);
    }

    private static bool TryString(JsonElement data, string name, out string value)
    {
        value = "";
        if (!data.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? "";
        return value.Length is > 0 and <= 200 && !value.Any(char.IsControl);
    }

    private static bool TryNullableString(JsonElement data, string name, out string? value)
    {
        value = null;
        if (!data.TryGetProperty(name, out var element)) return false;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return value is { Length: > 0 and <= 200 } && !value.Any(char.IsControl);
    }

    private static bool IsCanonicalUuid(string value) =>
        value.Length == 36 && Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsEvidence(string value, string rxHash)
    {
        var prefix = $"rxh-{rxHash[..16]}-";
        return value.StartsWith(prefix, StringComparison.Ordinal) &&
               value.Length >= prefix.Length + 10 &&
               value.Length <= prefix.Length + 13 &&
               value[prefix.Length..].All(ch => ch is >= '0' and <= '9');
    }
}
