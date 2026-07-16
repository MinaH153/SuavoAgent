using System.Text.Json;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.Cloud;

public sealed class SeedClient
{
    private static readonly JsonSerializerOptions WebJson =
        new(JsonSerializerDefaults.Web);
    private readonly IPostSigner _signer;

    public SeedClient(IPostSigner signer) => _signer = signer;

    public async Task<SeedResponse?> PullAsync(SeedRequest request, CancellationToken ct)
    {
        // H-11: Verify ECDSA signature on response body — cloud compromise cannot inject SQL shapes
        var result = await _signer.PostSignedVerifiedAsync(
            "/api/agent/seed/pull", request, SelfUpdater.SeedPublicKeyDer, ct);
        if (result is null) return null;
        var response = JsonSerializer.Deserialize<SeedResponse>(result.Value.GetRawText());
        if (response is null || !HasDeliveryAuthority(response, request.Phase))
            return null;
        return response;
    }

    internal async Task<bool> ConfirmAsync(
        SignedDeviceReceipt<SeedApplicationDeviceReceipt> signed,
        CancellationToken ct)
    {
        var response = await _signer.PostSignedVerifiedAsync(
            "/api/agent/seed/confirm",
            new
            {
                receipt = JsonSerializer.SerializeToElement(signed.Receipt, WebJson),
                keyId = signed.KeyId,
                signature = signed.Signature,
                canonicalDigest = signed.CanonicalDigest,
            },
            SelfUpdater.SeedPublicKeyDer,
            ct).ConfigureAwait(false);
        if (response is null || response.Value.ValueKind != JsonValueKind.Object)
            return false;
        var root = response.Value;
        var names = root.EnumerateObject().Select(property => property.Name).ToHashSet();
        return names.SetEquals(new[] { "success", "status", "commandId", "idempotent" }) &&
               root.TryGetProperty("success", out var success) &&
               success.ValueKind == JsonValueKind.True &&
               root.TryGetProperty("status", out var status) &&
               status.GetString() == "applied" &&
               root.TryGetProperty("commandId", out var commandId) &&
               commandId.GetString() == signed.Receipt.CommandId &&
               root.TryGetProperty("idempotent", out var idempotent) &&
               idempotent.ValueKind is JsonValueKind.True or JsonValueKind.False;
    }

    private static bool HasDeliveryAuthority(SeedResponse response, string requestedPhase)
    {
        if (!Guid.TryParseExact(response.CommandId, "D", out var commandId) ||
            !string.Equals(commandId.ToString("D"), response.CommandId, StringComparison.Ordinal) ||
            response.DeviceKeyId is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                response.DeviceKeyId, "^[a-f0-9]{64}$") ||
            response.SourceManifestDigest is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                response.SourceManifestDigest, "^[a-f0-9]{64}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                response.SeedDigest, "^[a-f0-9]{64}$") ||
            !string.Equals(response.Phase, requestedPhase, StringComparison.Ordinal) ||
            !DateTimeOffset.TryParse(response.ExpiresAt, out var expiresAt) ||
            expiresAt <= DateTimeOffset.UtcNow)
            return false;
        return true;
    }
}
