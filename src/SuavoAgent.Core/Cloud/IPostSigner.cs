using System.Text.Json;

namespace SuavoAgent.Core.Cloud;

public interface IPostSigner
{
    // Immutable local audience for signed cloud receipts. Callers fail closed
    // when a transport cannot expose both bindings.
    string? BoundAgentInstanceId => null;
    string? BoundPharmacyId => null;

    Task<JsonElement?> PostSignedAsync(
        string path,
        object payload,
        CancellationToken ct);

    /// <summary>
    /// Sends a signed request and accepts only a response with a valid pinned
    /// ECDSA body signature.
    /// </summary>
    Task<JsonElement?> PostSignedVerifiedAsync(
        string path,
        object payload,
        string publicKeyDer,
        CancellationToken ct);

    /// <summary>
    /// Verifies exact bounded response bytes for success and error statuses.
    /// </summary>
    Task<VerifiedCloudPostResponse?> PostSignedResponseVerifiedAsync(
        string path,
        object payload,
        CancellationToken ct) =>
        Task.FromResult<VerifiedCloudPostResponse?>(null);

    /// <summary>
    /// Signs an exact JSON string previously committed to a durable outbox and
    /// verifies the exact bounded response bytes with the pinned response key.
    /// </summary>
    Task<VerifiedCloudPostResponse?> PostSignedJsonResponseVerifiedAsync(
        string path,
        string exactJson,
        CancellationToken ct) =>
        Task.FromResult<VerifiedCloudPostResponse?>(null);
}

public sealed record VerifiedCloudPostResponse(
    int StatusCode,
    string Body,
    string BodySha256,
    string KeyId,
    string SignatureBase64);
