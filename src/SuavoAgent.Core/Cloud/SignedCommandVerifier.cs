using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Core.Cloud;

public record SignedCommand(
    string Command, string AgentId, string MachineFingerprint,
    string Timestamp, string Nonce, string KeyId, string Signature,
    string DataHash = "");

public record VerificationResult(bool IsValid, string? Reason = null);

public class SignedCommandVerifier
{
    private readonly Dictionary<string, ECDsa> _keys = new();
    private readonly string _agentId;
    private readonly string _fingerprint;
    private readonly Dictionary<string, DateTimeOffset> _usedNonces = new();
    private readonly TimeSpan _timestampWindow = TimeSpan.FromSeconds(30);

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
        var skew = (DateTimeOffset.UtcNow - ts).Duration();
        if (skew > _timestampWindow)
            return new(false, "Timestamp out of window");

        lock (_usedNonces)
        {
            if (_usedNonces.ContainsKey(cmd.Nonce))
                return new(false, "Nonce replay detected");
            _usedNonces[cmd.Nonce] = DateTimeOffset.UtcNow;
        }

        var dataHash = string.IsNullOrEmpty(cmd.DataHash) ? "" : cmd.DataHash;
        var canonical = $"{cmd.Command}|{cmd.AgentId}|{cmd.MachineFingerprint}|{cmd.Timestamp}|{cmd.Nonce}|{dataHash}";
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
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        lock (_usedNonces)
        {
            var expired = _usedNonces.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in expired) _usedNonces.Remove(key);
        }
    }

    /// <summary>
    /// Computes SHA-256 hex-lowercase hash of raw JSON data for canonical inclusion.
    /// Returns the hash of an empty string when <paramref name="dataJson"/> is null or empty.
    /// </summary>
    public static string ComputeDataHash(string? dataJson)
    {
        var input = string.IsNullOrEmpty(dataJson) ? "" : dataJson;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
