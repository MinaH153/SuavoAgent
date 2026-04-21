using System.Text;
using SuavoAgent.Verbs;
using SuavoAgent.Verbs.Signing;
using Xunit;

namespace SuavoAgent.Verbs.Tests.Signing;

public class HmacSignatureVerifierTests
{
    private static SignedVerbInvocation MakeInvocation(string sig) =>
        new(
            InvocationId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            VerbName: "restart_service",
            VerbVersion: "1.0.0",
            SchemaHash: "abc",
            Parameters: new Dictionary<string, object?> { ["service_name"] = "SuavoAgent.Core" },
            FenceId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            PharmacyId: "ph",
            SignedAt: 1712345678,
            Signature: sig);

    [Fact]
    public void Verify_CorrectSignature_ReturnsTrue()
    {
        byte[] key = Encoding.UTF8.GetBytes("test-key-32-bytes-long-padding!!");
        var inv = MakeInvocation("stub");
        var sig = HmacSignatureVerifier.ComputeSignature(inv, key);
        var signedInv = inv with { Signature = sig };

        var verifier = new HmacSignatureVerifier(new StaticKeyProvider(key));
        Assert.True(verifier.Verify(signedInv));
    }

    [Fact]
    public void Verify_WrongSignature_ReturnsFalse()
    {
        byte[] key = Encoding.UTF8.GetBytes("test-key-32-bytes-long-padding!!");
        var verifier = new HmacSignatureVerifier(new StaticKeyProvider(key));
        Assert.False(verifier.Verify(MakeInvocation("0000000000000000000000000000000000000000000000000000000000000000")));
    }

    [Fact]
    public void Verify_DifferentKey_ReturnsFalse()
    {
        byte[] key1 = Encoding.UTF8.GetBytes("test-key-32-bytes-long-padding!!");
        byte[] key2 = Encoding.UTF8.GetBytes("different-key-32-bytes-padding!!");
        var inv = MakeInvocation("stub");
        var sig = HmacSignatureVerifier.ComputeSignature(inv, key1);
        var signedInv = inv with { Signature = sig };

        var verifier = new HmacSignatureVerifier(new StaticKeyProvider(key2));
        Assert.False(verifier.Verify(signedInv));
    }

    [Fact]
    public void Verify_RotationGraceWindow_AcceptsPreviousKey()
    {
        byte[] oldKey = Encoding.UTF8.GetBytes("test-key-32-bytes-long-padding!!");
        byte[] newKey = Encoding.UTF8.GetBytes("new-key-32-bytes-long-padding!!!");
        var inv = MakeInvocation("stub");
        var oldSig = HmacSignatureVerifier.ComputeSignature(inv, oldKey);
        var signedInv = inv with { Signature = oldSig };

        var dualProvider = new DualKeyProvider(newKey, oldKey);
        var verifier = new HmacSignatureVerifier(dualProvider);
        Assert.True(verifier.Verify(signedInv));
    }

    [Fact]
    public void ComputeSignature_IsDeterministic()
    {
        byte[] key = new byte[32];
        var inv = MakeInvocation("stub");
        var s1 = HmacSignatureVerifier.ComputeSignature(inv, key);
        var s2 = HmacSignatureVerifier.ComputeSignature(inv, key);
        Assert.Equal(s1, s2);
    }

    [Fact]
    public void ComputeSignature_TimestampAffectsSignature()
    {
        byte[] key = new byte[32];
        var inv1 = MakeInvocation("stub");
        var inv2 = inv1 with { SignedAt = inv1.SignedAt + 1 };
        Assert.NotEqual(
            HmacSignatureVerifier.ComputeSignature(inv1, key),
            HmacSignatureVerifier.ComputeSignature(inv2, key));
    }

    private sealed class DualKeyProvider : IKeyProvider
    {
        private readonly byte[] _cur;
        private readonly byte[] _prev;
        public DualKeyProvider(byte[] cur, byte[] prev) { _cur = cur; _prev = prev; }
        public byte[] CurrentKey() => _cur;
        public byte[]? PreviousKey() => _prev;
    }
}
