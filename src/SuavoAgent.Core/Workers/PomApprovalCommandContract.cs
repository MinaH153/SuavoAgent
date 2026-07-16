using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.Workers;

internal sealed record PomApprovalCommand(
    int SchemaVersion,
    string CommandId,
    string PomId,
    string SessionId,
    string ApprovedModelDigest,
    string ApprovedTemplateDigest,
    string ApprovedBy,
    DateTimeOffset ExpiresAt,
    string PayloadDigest);

/// <summary>
/// Exact PHI-free schema for the cloud-to-workstation POM approval receipt.
/// The stable command id is the idempotency key; the SHA-256 of the exact data
/// JSON detects a server bug or replay that reuses an id for different bytes.
/// </summary>
internal static class PomApprovalCommandContract
{
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "pomId",
        "sessionId",
        "approvedModelDigest",
        "approvedTemplateDigest",
        "approvedBy",
        "commandId",
        "expiresAt",
    };

    private static readonly Regex SessionToken = new(
        @"^[a-z0-9][a-z0-9._-]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static bool TryParse(
        JsonElement data,
        out PomApprovalCommand? command,
        out string rejectionCode)
    {
        command = null;
        rejectionCode = "pom_approval_schema_invalid";
        if (data.ValueKind != JsonValueKind.Object || !HasExactProperties(data) ||
            !data.TryGetProperty("schemaVersion", out var schema) ||
            schema.ValueKind != JsonValueKind.Number ||
            !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1 ||
            !TryCanonicalUuid(data, "commandId", out var commandId) ||
            !TryCanonicalUuid(data, "pomId", out var pomId) ||
            !TryString(data, "sessionId", out var sessionId) ||
            !SessionToken.IsMatch(sessionId) ||
            !TryString(data, "approvedModelDigest", out var modelDigest) ||
            !IsLowerSha256(modelDigest) ||
            !TryString(data, "approvedTemplateDigest", out var templateDigest) ||
            !IsLowerSha256(templateDigest) ||
            !TryCanonicalUuid(data, "approvedBy", out var approvedBy) ||
            !TryInstant(data, "expiresAt", out var expiresAt))
        {
            return false;
        }

        command = new PomApprovalCommand(
            schemaVersion,
            commandId,
            pomId,
            sessionId,
            modelDigest,
            templateDigest,
            approvedBy,
            expiresAt,
            ComputePayloadDigest(data));
        rejectionCode = "";
        return true;
    }

    internal static bool TryGetLedgerIdentity(
        JsonElement data,
        out string commandId,
        out string payloadDigest)
    {
        payloadDigest = "";
        if (data.ValueKind != JsonValueKind.Object ||
            !TryCanonicalUuid(data, "commandId", out commandId))
        {
            commandId = "";
            return false;
        }

        payloadDigest = ComputePayloadDigest(data);
        return true;
    }

    internal static string ComputePayloadDigest(JsonElement data)
        => DeviceAuthorityCanonical.HashPomCommand(data);

    internal static bool IsSafeResultCode(string value) =>
        value.Length is > 0 and <= 80 &&
        value.All(ch => ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    internal static bool IsExpired(PomApprovalCommand command, DateTimeOffset now) =>
        now.ToUniversalTime() > command.ExpiresAt.ToUniversalTime();

    private static bool HasExactProperties(JsonElement data)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
            if (!names.Add(property.Name)) return false;
        return names.SetEquals(ExactFields);
    }

    private static bool TryString(JsonElement data, string name, out string value)
    {
        value = "";
        if (!data.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? "";
        return value.Length is > 0 and <= 200 && !value.Any(char.IsControl);
    }

    private static bool TryCanonicalUuid(JsonElement data, string name, out string value) =>
        TryString(data, name, out value) &&
        value.Length == 36 &&
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool IsLowerSha256(string value) =>
        value.Length == 64 &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryInstant(JsonElement data, string name, out DateTimeOffset value)
    {
        value = default;
        return TryString(data, name, out var raw) &&
               raw.Length <= 40 &&
               (raw.EndsWith('Z') || Regex.IsMatch(raw, @"[+-]\d{2}:?\d{2}$")) &&
               DateTimeOffset.TryParse(
                   raw,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out value);
    }
}
