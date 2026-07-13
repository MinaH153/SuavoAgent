using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Core.Cloud;

public sealed partial class SuavoCloudClient
{
    public async Task<VerifiedCloudPostResponse?> PostSignedResponseVerifiedAsync(
        string path,
        object payload,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(payload);
        return await PostSignedJsonResponseVerifiedAsync(path, body, ct)
            .ConfigureAwait(false);
    }

    public async Task<VerifiedCloudPostResponse?> PostSignedJsonResponseVerifiedAsync(
        string path,
        string exactJson,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactJson);
        OutboundPhiGuard.AssertAllowed(path, exactJson, _options);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Content = new StringContent(exactJson, Encoding.UTF8, "application/json");
        _signer.ApplyHeaders(request, exactJson);

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);
        var responseBytes = await ReadResponseBytesBoundedAsync(
            response, MaxErrorResponseBytes, ct).ConfigureAwait(false);
        if (responseBytes is null) return null;
        try
        {
            if (!response.Headers.TryGetValues("X-Response-Signature", out var values) ||
                values.ToArray() is not [var signature] ||
                !VerifyEcdsaSignature(
                    responseBytes, signature, _callbackResponsePublicKeyDer))
                return null;
            var digest = SHA256.HashData(responseBytes);
            try
            {
                return new VerifiedCloudPostResponse(
                    (int)response.StatusCode,
                    Encoding.UTF8.GetString(responseBytes),
                    Convert.ToHexString(digest).ToLowerInvariant(),
                    RemoteCommandTrust.CommandV1KeyId,
                    signature);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(responseBytes);
        }
    }
}
