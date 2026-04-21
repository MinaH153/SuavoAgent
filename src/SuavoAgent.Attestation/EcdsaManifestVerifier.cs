using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Attestation;

/// <summary>
/// ECDSA P-256 signature verifier. Uses the embedded public key passed in
/// via constructor — in production this is the agent's baked-in public key
/// from the release signing key pair (key-custody.md §Release signing key).
/// </summary>
public sealed class EcdsaManifestVerifier : IManifestSignatureVerifier
{
    private readonly byte[] _publicKeySpki;

    public EcdsaManifestVerifier(byte[] publicKeySpki)
    {
        _publicKeySpki = publicKeySpki ?? throw new ArgumentNullException(nameof(publicKeySpki));
    }

    public bool Verify(string manifestJson, byte[] signatureBytes)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(_publicKeySpki, out _);
            var bodyBytes = Encoding.UTF8.GetBytes(manifestJson);
            return ecdsa.VerifyData(bodyBytes, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}
