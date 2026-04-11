using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Core.Cloud;

public record SignedCommand(
    string Command, string AgentId, string MachineFingerprint,
    string Timestamp, string Nonce, string KeyId, string Signature);

public record VerificationResult(bool IsValid, string? Reason = null);

public class SignedCommandVerifier
{
    private readonly Dictionary<string, ECDsa> _keys = new();
    private readonly string _agentId;
    private readonly string _fingerprint;
    private readonly HashSet<string> _usedNonces = new();
    private readonly TimeSpan _timestampWindow = TimeSpan.FromSeconds(300);

    public SignedCommandVerifier(
        Dictionary<string, string> keyRegistry,
        string agentId, string fingerprint)
    {
        _agentId = agentId;
        _fingerprint = fingerprint;

        foreach (var (keyId, pubKeyDer) in keyRegistry)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubKeyDer), out _);
            _keys[keyId] = ecdsa;
        }
    }

    public VerificationResult Verify(SignedCommand cmd)
    {
        if (!_keys.TryGetValue(cmd.KeyId, out var key))
            return new(false, $"Unknown keyId: {cmd.KeyId}");

        if (!string.Equals(cmd.AgentId, _agentId, StringComparison.Ordinal))
            return new(false, "AgentId mismatch");

        if (!string.Equals(cmd.MachineFingerprint, _fingerprint, StringComparison.Ordinal))
            return new(false, "Fingerprint mismatch");

        if (!DateTimeOffset.TryParse(cmd.Timestamp, out var ts))
            return new(false, "Invalid timestamp format");
        if (DateTimeOffset.UtcNow - ts > _timestampWindow)
            return new(false, "Timestamp expired");

        lock (_usedNonces)
        {
            if (!_usedNonces.Add(cmd.Nonce))
                return new(false, "Nonce replay detected");
        }

        var canonical = $"{cmd.Command}|{cmd.AgentId}|{cmd.MachineFingerprint}|{cmd.Timestamp}|{cmd.Nonce}";
        try
        {
            var valid = key.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                Convert.FromBase64String(cmd.Signature),
                HashAlgorithmName.SHA256);
            return valid ? new(true) : new(false, "Invalid signature");
        }
        catch
        {
            return new(false, "Signature verification error");
        }
    }

    public void PruneNonces(TimeSpan maxAge)
    {
        lock (_usedNonces) { _usedNonces.Clear(); }
    }
}
