using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Workers;

internal static class PricingApprovalResponseContract
{
    internal const int MaximumReceiptsPerHeartbeat = 20;

    private static readonly HashSet<string> ReceiptFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "proposalId",
        "proposalDigest",
        "pharmacyId",
        "agentId",
        "machineFingerprint",
        "receivedAtUtc",
        "keyId",
        "signature",
    };

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    internal static bool TryParseProposalReceipts(
        JsonElement value,
        out IReadOnlyList<PricingApprovalProposalReceipt> receipts,
        out string code)
    {
        receipts = Array.Empty<PricingApprovalProposalReceipt>();
        code = "pricing_approval_proposal_receipts_schema_invalid";
        if (value.ValueKind != JsonValueKind.Array)
            return false;

        var elements = value.EnumerateArray().ToArray();
        if (elements.Length > MaximumReceiptsPerHeartbeat)
        {
            code = "pricing_approval_proposal_receipts_limit_exceeded";
            return false;
        }

        var parsed = new List<PricingApprovalProposalReceipt>(elements.Length);
        var proposalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in elements)
        {
            if (!HasExactUniqueFields(element))
                return false;
            try
            {
                var receipt = element.Deserialize<PricingApprovalProposalReceipt>(StrictJson);
                if (receipt is null || !proposalIds.Add(receipt.ProposalId))
                    return false;
                parsed.Add(receipt);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        receipts = parsed;
        code = "valid";
        return true;
    }

    private static bool HasExactUniqueFields(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!fields.Add(property.Name)) return false;
        return fields.SetEquals(ReceiptFields);
    }
}
