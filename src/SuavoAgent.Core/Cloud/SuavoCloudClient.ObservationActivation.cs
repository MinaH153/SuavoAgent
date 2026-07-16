using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.Cloud;

public sealed partial class SuavoCloudClient
{
    internal const string ObservationActivationLeasePath =
        "/api/agent/observation-activation/lease";

    private static readonly Regex ExactActivationTimestamp = new(
        "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal async Task<ObservationActivationState?>
        RequestObservationActivationLeaseAsync(
            IObservationActivationRequestSigner requestSigner,
            ObservationActivationAuthority authority,
            CancellationToken cancellationToken)
    {
        var request = requestSigner.Create(authority.GetKnownGeneration());
        var response = await PostSignedAsync(
            ObservationActivationLeasePath,
            request,
            cancellationToken).ConfigureAwait(false);
        return response is { } value && TryParseObservationActivationLease(
                value,
                request.CanonicalDigest,
                out var state)
            ? state
            : null;
    }

    internal static bool TryParseObservationActivationLease(
        JsonElement response,
        string expectedRequestDigest,
        out ObservationActivationState? state)
    {
        state = null;
        if (response.ValueKind != JsonValueKind.Object ||
            !HasExactPropertyOrder(
                response,
                "command", "agentId", "machineFingerprint", "timestamp",
                "nonce", "keyId", "signature", "dataHash", "data") ||
            !TryReadString(response, "command", out var command) ||
            !TryReadString(response, "agentId", out var agentId) ||
            !TryReadString(response, "machineFingerprint", out var machineFingerprint) ||
            !TryReadString(response, "timestamp", out var timestamp) ||
            !TryReadString(response, "nonce", out var nonce) ||
            !TryReadString(response, "keyId", out var keyId) ||
            !TryReadString(response, "signature", out var signature) ||
            !TryReadString(response, "dataHash", out var dataHash) ||
            !response.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !HasExactPropertyOrder(
                data,
                "schemaVersion", "leaseId", "requestId", "requestDigest",
                "pharmacyId", "workstationId",
                "deviceKeyId", "releaseCohort", "generation", "policyDigest",
                "issuedAtUtc", "notBeforeUtc", "expiresAtUtc", "authorizationId") ||
            !TryReadString(data, "requestDigest", out var requestDigest) ||
            !TryReadString(data, "issuedAtUtc", out var issuedAtUtc) ||
            !TryReadString(data, "notBeforeUtc", out var notBeforeUtc) ||
            !TryReadString(data, "expiresAtUtc", out var expiresAtUtc) ||
            !ExactActivationTimestamp.IsMatch(timestamp) ||
            !string.Equals(timestamp, issuedAtUtc, StringComparison.Ordinal) ||
            !string.Equals(timestamp, notBeforeUtc, StringComparison.Ordinal) ||
            !ExactActivationTimestamp.IsMatch(expiresAtUtc) ||
            !IsLowerHex64(expectedRequestDigest) ||
            !FixedAsciiEquals(requestDigest, expectedRequestDigest) ||
            !IsLowerCanonicalV4Uuid(nonce))
            return false;

        state = new ObservationActivationState(
            ObservationActivationAuthority.CurrentSchemaVersion,
            new ObservationActivationSignedLease(
                command,
                agentId,
                machineFingerprint,
                timestamp,
                nonce,
                keyId,
                signature,
                dataHash,
                data.GetRawText()));
        return true;
    }

    private static bool HasExactPropertyOrder(JsonElement element, params string[] expected)
    {
        var index = 0;
        foreach (var property in element.EnumerateObject())
        {
            if (index >= expected.Length ||
                !string.Equals(property.Name, expected[index], StringComparison.Ordinal))
                return false;
            index++;
        }
        return index == expected.Length;
    }

    private static bool IsLowerCanonicalV4Uuid(string value) =>
        value.Length == 36 && value[14] == '4' && value[19] is '8' or '9' or 'a' or 'b' &&
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool IsLowerHex64(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedAsciiEquals(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length ||
            !left.All(char.IsAscii) || !right.All(char.IsAscii))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));
    }
}
