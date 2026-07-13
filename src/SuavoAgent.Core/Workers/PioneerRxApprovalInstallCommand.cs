using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.Workers;

internal sealed record PioneerRxApprovalInstallCommand(
    string CommandId,
    int ProtocolEpoch,
    PioneerRxProcessApprovalReceipt Receipt,
    PioneerRxApprovalAuthorityState Authority,
    PioneerRxVendorIdentityCatalog VendorCatalog,
    string PayloadDigest);

internal static class PioneerRxApprovalInstallCommandContract
{
    internal const string CommandName = "install_pioneerrx_process_approval";
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "protocolEpoch",
        "commandId",
        "receipt",
        "authority",
        "vendorCatalog",
    };
    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    internal static bool TryParse(
        JsonElement data,
        out PioneerRxApprovalInstallCommand? command,
        out string code)
    {
        command = null;
        code = "pioneerrx_approval_command_schema_invalid";
        if (data.ValueKind != JsonValueKind.Object || !HasUniquePropertiesRecursive(data) ||
            !data.EnumerateObject().Select(item => item.Name).ToHashSet(StringComparer.Ordinal)
                .SetEquals(ExactFields) ||
            !data.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1 ||
            !data.TryGetProperty("protocolEpoch", out var epoch) ||
            epoch.ValueKind != JsonValueKind.Number || !epoch.TryGetInt32(out var protocolEpoch) ||
            protocolEpoch != PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch ||
            !TryCanonicalUuid(data, "commandId", out var commandId) ||
            !data.TryGetProperty("receipt", out var receiptElement) ||
            receiptElement.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("authority", out var authorityElement) ||
            authorityElement.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("vendorCatalog", out var catalogElement) ||
            catalogElement.ValueKind != JsonValueKind.Object)
            return false;

        try
        {
            var receipt = receiptElement.Deserialize<PioneerRxProcessApprovalReceipt>(StrictJson);
            var authority = authorityElement.Deserialize<PioneerRxApprovalAuthorityState>(StrictJson);
            var vendorCatalog = catalogElement.Deserialize<PioneerRxVendorIdentityCatalog>(StrictJson);
            if (receipt is null || authority is null || vendorCatalog is null) return false;
                command = new PioneerRxApprovalInstallCommand(
                    commandId,
                    protocolEpoch,
                    receipt,
                    authority,
                    vendorCatalog,
                    PioneerRxApprovalMaintenanceContract.ComputePayloadDigest(
                        commandId,
                        receipt,
                        authority,
                        vendorCatalog,
                        protocolEpoch));
            code = "valid";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasUniquePropertiesRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || !HasUniquePropertiesRecursive(property.Value))
                    return false;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (!HasUniquePropertiesRecursive(item)) return false;
        }
        return true;
    }

    private static bool TryCanonicalUuid(JsonElement data, string name, out string value)
    {
        value = string.Empty;
        return data.TryGetProperty(name, out var element) &&
               element.ValueKind == JsonValueKind.String &&
               (value = element.GetString() ?? string.Empty).Length == 36 &&
               Guid.TryParseExact(value, "D", out var parsed) &&
               string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);
    }
}
