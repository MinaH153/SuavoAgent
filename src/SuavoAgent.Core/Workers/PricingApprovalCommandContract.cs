using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Workers;

internal sealed record PricingApprovalInstallCommand(
    string CommandId,
    PricingApprovalGrant Grant);

internal sealed record PricingApprovalRevokeCommand(
    string CommandId,
    PricingApprovalRevocation Revocation);

internal static class PricingApprovalCommandContract
{
    private static readonly HashSet<string> InstallFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "commandId",
        "grant",
    };

    private static readonly HashSet<string> RevokeFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "commandId",
        "revocation",
    };

    private static readonly JsonSerializerOptions StrictJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    internal static bool TryParseInstall(
        JsonElement data,
        out PricingApprovalInstallCommand? command,
        out string code)
    {
        command = null;
        code = "pricing_approval_install_schema_invalid";
        if (!ValidEnvelopeObject(data, InstallFields, out var commandId) ||
            !data.TryGetProperty("grant", out var grantElement) ||
            grantElement.ValueKind != JsonValueKind.Object)
            return false;
        try
        {
            var grant = grantElement.Deserialize<PricingApprovalGrant>(StrictJson);
            if (grant is null) return false;
            command = new(commandId, grant);
            code = "valid";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseRevoke(
        JsonElement data,
        out PricingApprovalRevokeCommand? command,
        out string code)
    {
        command = null;
        code = "pricing_approval_revoke_schema_invalid";
        if (!ValidEnvelopeObject(data, RevokeFields, out var commandId) ||
            !data.TryGetProperty("revocation", out var revocationElement) ||
            revocationElement.ValueKind != JsonValueKind.Object)
            return false;
        try
        {
            var revocation = revocationElement.Deserialize<PricingApprovalRevocation>(StrictJson);
            if (revocation is null) return false;
            command = new(commandId, revocation);
            code = "valid";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ValidEnvelopeObject(
        JsonElement data,
        IReadOnlySet<string> exactFields,
        out string commandId)
    {
        commandId = string.Empty;
        if (data.ValueKind != JsonValueKind.Object ||
            !HasUniquePropertiesRecursive(data) ||
            !data.EnumerateObject().Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(exactFields) ||
            !data.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var version) ||
            version != PricingApprovalContract.SchemaVersion ||
            !data.TryGetProperty("commandId", out var id) ||
            id.ValueKind != JsonValueKind.String)
            return false;
        commandId = id.GetString() ?? string.Empty;
        return Guid.TryParseExact(commandId, "D", out var parsed) &&
               string.Equals(parsed.ToString("D"), commandId, StringComparison.Ordinal);
    }

    private static bool HasUniquePropertiesRecursive(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) ||
                    !HasUniquePropertiesRecursive(property.Value))
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
}
