using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class SignedCommandVerifierTests
{
    private readonly ECDsa _signingKey;
    private readonly SignedCommandVerifier _verifier;
    private const string AgentId = "agent-test-123";
    private const string Fingerprint = "fp-test-456";

    public SignedCommandVerifierTests()
    {
        _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pubKeyDer = Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo());
        _verifier = new SignedCommandVerifier(
            new Dictionary<string, string> { { "test-key-v1", pubKeyDer } },
            AgentId, Fingerprint);
    }

    private SignedCommand CreateSignedCommand(string command, string? agentId = null,
        string? fingerprint = null, string? keyId = null, DateTimeOffset? timestamp = null)
    {
        agentId ??= AgentId;
        fingerprint ??= Fingerprint;
        keyId ??= "test-key-v1";
        var ts = (timestamp ?? DateTimeOffset.UtcNow).ToString("o");
        var nonce = Guid.NewGuid().ToString();
        var canonical = $"{command}|{agentId}|{fingerprint}|{ts}|{nonce}";
        var sig = Convert.ToBase64String(
            _signingKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
        return new SignedCommand(command, agentId, fingerprint, ts, nonce, keyId, sig);
    }

    [Fact]
    public void Verify_ValidCommand_Succeeds()
    {
        var cmd = CreateSignedCommand("force_sync");
        Assert.True(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_WrongAgentId_Fails()
    {
        var cmd = CreateSignedCommand("force_sync", agentId: "wrong-agent");
        Assert.False(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_WrongFingerprint_Fails()
    {
        var cmd = CreateSignedCommand("force_sync", fingerprint: "wrong-fp");
        Assert.False(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_ExpiredTimestamp_Fails()
    {
        var cmd = CreateSignedCommand("force_sync",
            timestamp: DateTimeOffset.UtcNow.AddSeconds(-400));
        Assert.False(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_UnknownKeyId_Fails()
    {
        var cmd = CreateSignedCommand("force_sync", keyId: "unknown-key");
        Assert.False(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_ReplayedNonce_Fails()
    {
        var cmd = CreateSignedCommand("force_sync");
        _verifier.Verify(cmd);
        Assert.False(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_TamperedSignature_Fails()
    {
        var cmd = CreateSignedCommand("force_sync") with { Signature = Convert.ToBase64String(new byte[64]) };
        Assert.False(_verifier.Verify(cmd).IsValid);
    }
}
