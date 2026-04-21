namespace SuavoAgent.Attestation;

public interface IManifestFetcher
{
    /// <summary>
    /// Fetches signed manifest for the given version from the cloud attestation
    /// endpoint. Returns manifest + signature bytes, or null on network failure.
    /// </summary>
    Task<SignedManifestPayload?> FetchAsync(string version, CancellationToken cancellationToken);
}

public sealed record SignedManifestPayload(
    string ManifestJson,
    byte[] SignatureBytes);
