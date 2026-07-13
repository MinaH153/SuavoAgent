using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal bool TryReadVerifiedPricingCheckpoint(
        string commandId,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        out SignedCommand? verifiedCommand)
    {
        verifiedCommand = null;
        string commandKind;
        string agentId;
        string machineFingerprint;
        string timestamp;
        string nonce;
        string dataHash;
        string keyId;
        string signature;
        string expiresAt;
        string checkpointDigest;
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT command_kind, signed_agent_id,
                       signed_machine_fingerprint, signed_timestamp,
                       signed_nonce, signed_data_hash, signed_key_id,
                       signed_signature, signed_expires_at,
                       signed_checkpoint_digest
                  FROM pricing_command_execution_intents
                 WHERE command_id = @command
                """;
            command.Parameters.AddWithValue("@command", commandId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || Enumerable.Range(1, 9).Any(reader.IsDBNull))
                return false;
            commandKind = reader.GetString(0);
            agentId = reader.GetString(1);
            machineFingerprint = reader.GetString(2);
            timestamp = reader.GetString(3);
            nonce = reader.GetString(4);
            dataHash = reader.GetString(5);
            keyId = reader.GetString(6);
            signature = reader.GetString(7);
            expiresAt = reader.GetString(8);
            checkpointDigest = reader.GetString(9);
        }

        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            commandKind, agentId, machineFingerprint, timestamp, nonce, dataHash);
        var expectedDigest = CheckpointDigest(
            canonical, keyId, signature, expiresAt, commandId);
        if (!FixedHexEquals(expectedDigest, checkpointDigest) ||
            !trustedPublicKeys.TryGetValue(keyId, out var publicKey) ||
            !ValidSignedPricingExpiry(timestamp, expiresAt))
            return false;
        try
        {
            var signatureBytes = Convert.FromBase64String(signature);
            if (signatureBytes.Length != 64) return false;
            using var verifier = ECDsa.Create();
            var keyBytes = Convert.FromBase64String(publicKey);
            verifier.ImportSubjectPublicKeyInfo(keyBytes, out var bytesRead);
            if (bytesRead != keyBytes.Length || verifier.KeySize != 256 ||
                !verifier.VerifyData(
                    Encoding.UTF8.GetBytes(canonical),
                    signatureBytes,
                    HashAlgorithmName.SHA256))
                return false;
            verifiedCommand = new SignedCommand(
                commandKind, agentId, machineFingerprint, timestamp, nonce,
                keyId, signature, dataHash, expiresAt);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private sealed record SignedPricingCheckpoint(
        string AgentId,
        string MachineFingerprint,
        string Timestamp,
        string Nonce,
        string DataHash,
        string KeyId,
        string Signature,
        string ExpiresAt,
        string Digest);

    private static SignedPricingCheckpoint? BuildSignedCheckpoint(
        string nonce,
        string commandId,
        string commandKind,
        SignedCommand? command)
    {
        if (command is null) return null;
        if (command.Command != commandKind || command.Nonce != nonce ||
            !SafeCanonicalField(command.AgentId, 200) ||
            !SafeCanonicalField(command.MachineFingerprint, 256) ||
            !SafeCanonicalField(command.Timestamp, 64) ||
            !SafeCanonicalField(command.Nonce, 200) ||
            !IsLowerHex64(command.DataHash) ||
            !SafeCanonicalField(command.KeyId, 64) ||
            !ValidSignedPricingExpiry(command.Timestamp, command.ExpiresAt))
            throw new ArgumentException("Signed pricing checkpoint is invalid.");
        try
        {
            if (Convert.FromBase64String(command.Signature).Length != 64)
                throw new ArgumentException("Signed pricing checkpoint is invalid.");
        }
        catch (FormatException ex)
        {
            throw new ArgumentException(
                "Signed pricing checkpoint is invalid.", ex);
        }

        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            commandKind,
            command.AgentId,
            command.MachineFingerprint,
            command.Timestamp,
            command.Nonce,
            command.DataHash);
        return new(
            command.AgentId,
            command.MachineFingerprint,
            command.Timestamp,
            command.Nonce,
            command.DataHash,
            command.KeyId,
            command.Signature,
            command.ExpiresAt!,
            CheckpointDigest(
                canonical,
                command.KeyId,
                command.Signature,
                command.ExpiresAt!,
                commandId));
    }

    private static string CheckpointDigest(
        string canonical,
        string keyId,
        string signature,
        string expiresAt,
        string commandId) => Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                $"pricing_signed_admission_v2|{canonical}|{keyId}|{signature}|{expiresAt}|{commandId}")))
            .ToLowerInvariant();

    private static bool ValidSignedPricingExpiry(
        string timestamp,
        string? expiresAt) =>
        DateTimeOffset.TryParse(
            timestamp,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var issued) &&
        DateTimeOffset.TryParse(
            expiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var expires) &&
        expires > issued &&
        expires - issued <= TimeSpan.FromMinutes(5);

    private static bool SafeCanonicalField(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => character is >= ' ' and <= '~' && character != '|');

    private static bool IsLowerHex64(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedHexEquals(string left, string right)
    {
        if (!IsLowerHex64(left) || !IsLowerHex64(right)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right));
    }
}
