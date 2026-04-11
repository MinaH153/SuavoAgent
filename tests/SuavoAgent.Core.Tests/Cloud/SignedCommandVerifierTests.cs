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
        string? fingerprint = null, string? keyId = null, DateTimeOffset? timestamp = null,
        string? dataJson = null)
    {
        agentId ??= AgentId;
        fingerprint ??= Fingerprint;
        keyId ??= "test-key-v1";
        var ts = (timestamp ?? DateTimeOffset.UtcNow).ToString("o");
        var nonce = Guid.NewGuid().ToString();
        var dataHash = SignedCommandVerifier.ComputeDataHash(dataJson);
        var canonical = $"{command}|{agentId}|{fingerprint}|{ts}|{nonce}|{dataHash}";
        var sig = Convert.ToBase64String(
            _signingKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
        return new SignedCommand(command, agentId, fingerprint, ts, nonce, keyId, sig, dataHash);
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
    public void Verify_FutureTimestamp_Fails()
    {
        var cmd = CreateSignedCommand("force_sync",
            timestamp: DateTimeOffset.UtcNow.AddSeconds(400));
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

    [Fact]
    public void Verify_WithDataPayload_Succeeds()
    {
        var data = """{"rxNumber":"12345","requesterId":"user-1"}""";
        var cmd = CreateSignedCommand("fetch_patient", dataJson: data);
        Assert.True(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_TamperedDataPayload_Fails()
    {
        var originalData = """{"rxNumber":"12345"}""";
        var cmd = CreateSignedCommand("fetch_patient", dataJson: originalData);
        // Attacker swaps data hash to match a different payload
        var tamperedHash = SignedCommandVerifier.ComputeDataHash("""{"rxNumber":"99999"}""");
        var tampered = cmd with { DataHash = tamperedHash };
        Assert.False(_verifier.Verify(tampered).IsValid);
    }

    [Fact]
    public void ComputeDataHash_NullAndEmpty_ProduceSameHash()
    {
        var nullHash = SignedCommandVerifier.ComputeDataHash(null);
        var emptyHash = SignedCommandVerifier.ComputeDataHash("");
        Assert.Equal(nullHash, emptyHash);
        Assert.Equal(64, nullHash.Length); // SHA-256 hex = 64 chars
    }
}
