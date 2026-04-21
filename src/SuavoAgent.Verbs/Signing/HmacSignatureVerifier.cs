using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Verbs.Signing;

/// <summary>
/// HMAC-SHA256 signature verifier for inbound <see cref="SignedVerbInvocation"/>.
/// Honors dual-key acceptance during rotation grace window.
///
/// Signature covers canonical JSON of the invocation fields (excluding
/// <c>Signature</c>) concatenated with the <c>SignedAt</c> unix timestamp
/// as a replay-defense salt.
/// </summary>
public sealed class HmacSignatureVerifier : ISignatureVerifier
{
    private readonly IKeyProvider _keyProvider;

    public HmacSignatureVerifier(IKeyProvider keyProvider)
    {
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
    }

    public bool Verify(SignedVerbInvocation invocation)
    {
        var expected = ComputeSignature(invocation, _keyProvider.CurrentKey());
        if (CryptoEquals(expected, invocation.Signature))
            return true;

        // Rotation grace: accept previous key as well.
        var prev = _keyProvider.PreviousKey();
        if (prev is not null)
        {
            var expectedPrev = ComputeSignature(invocation, prev);
            if (CryptoEquals(expectedPrev, invocation.Signature))
                return true;
        }

        return false;
    }

    internal static string ComputeSignature(SignedVerbInvocation invocation, byte[] key)
    {
        // Canonical body excludes the signature field itself.
        var canonical = new
        {
            invocation_id = invocation.InvocationId,
            verb_name = invocation.VerbName,
            verb_version = invocation.VerbVersion,
            schema_hash = invocation.SchemaHash,
            parameters = invocation.Parameters,
            fence_id = invocation.FenceId,
            pharmacy_id = invocation.PharmacyId,
            signed_at = invocation.SignedAt
        };
        var json = JsonSerializer.Serialize(canonical);
        var bodyBytes = Encoding.UTF8.GetBytes(json);
        using var hmac = new HMACSHA256(key);
        var mac = hmac.ComputeHash(bodyBytes);
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    private static bool CryptoEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var result = 0;
        for (var i = 0; i < a.Length; i++) result |= a[i] ^ b[i];
        return result == 0;
    }
}
