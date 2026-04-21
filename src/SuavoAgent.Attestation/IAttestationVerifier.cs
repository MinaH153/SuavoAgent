namespace SuavoAgent.Attestation;

public interface IAttestationVerifier
{
    /// <summary>
    /// Perform one attestation check: fetch manifest → verify signature →
    /// hash every file in the install dir → compare. Returns result.
    /// </summary>
    Task<AttestationResult> VerifyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Verifies ECDSA P-256 signatures over the manifest body. Implementation
/// uses the same embedded public key as the OTA update pipeline.
/// </summary>
public interface IManifestSignatureVerifier
{
    bool Verify(string manifestJson, byte[] signatureBytes);
}
