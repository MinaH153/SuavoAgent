using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    internal const string ReleaseNoopPurpose = "suavoagent-same-version-ota-noop";

    internal sealed record PersistedReleaseNoopDeviceReceipt(
        SignedDeviceReceipt<ReleaseNoopDeviceReceipt> Signed);

    internal PersistedReleaseNoopDeviceReceipt GetOrCreateReleaseNoopDeviceReceipt(
        ReleaseNoopDeviceReceipt receipt,
        IDeviceAuthoritySigner signer)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(signer);
        ValidateReleaseNoopReceipt(receipt, signer.KeyId);

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadReleaseNoopDeviceReceipt(
                transaction,
                receipt.CommandId,
                receipt.EnvelopeNonce);
            if (existing is not null)
            {
                transaction.Commit();
                if (!HasSameReleaseNoopBinding(existing.Signed.Receipt, receipt) ||
                    !string.Equals(existing.Signed.KeyId, signer.KeyId, StringComparison.Ordinal))
                    throw new InvalidOperationException("Release no-op device receipt replay conflict.");
                return existing;
            }

            RequireRegisteredUpdateBinding(transaction, receipt);
            var signed = signer.Sign(receipt);
            if (!string.Equals(signed.KeyId, signer.KeyId, StringComparison.Ordinal) ||
                !IsLowerSha256(signed.KeyId) ||
                !IsLowerSha256(signed.CanonicalDigest) ||
                !IsBase64UrlP1363(signed.Signature))
                throw new InvalidOperationException("Release no-op device signature is malformed.");

            using var insert = CreateCommand(transaction, """
                INSERT INTO update_noop_device_receipts (
                    command_id, envelope_nonce, data_hash, target_version,
                    ota_signing_key_id, manifest_canonical, manifest_signature,
                    release_tag, source_sha, manifest_name, checksums_sha256,
                    checksums_signature_sha256, inventory_sha256,
                    install_receipt_sha256, restart_receipt_sha256,
                    device_key_id, receipt_json, device_signature, canonical_digest,
                    verified_at_utc, committed_at_utc
                ) VALUES (
                    @commandId, @nonce, @dataHash, @targetVersion,
                    @otaSigningKeyId, @manifestCanonical, @manifestSignature,
                    @releaseTag, @sourceSha, @manifestName, @checksumsSha256,
                    @checksumsSignatureSha256, @inventorySha256,
                    @installReceiptSha256, @restartReceiptSha256,
                    @deviceKeyId, @receiptJson, @deviceSignature, @canonicalDigest,
                    @verifiedAtUtc, @committedAtUtc
                )
                """);
            insert.Parameters.AddWithValue("@commandId", receipt.CommandId);
            insert.Parameters.AddWithValue("@nonce", receipt.EnvelopeNonce);
            insert.Parameters.AddWithValue("@dataHash", receipt.CommandDataHash);
            insert.Parameters.AddWithValue("@targetVersion", receipt.TargetVersion);
            insert.Parameters.AddWithValue("@otaSigningKeyId", receipt.OtaSigningKeyId);
            insert.Parameters.AddWithValue("@manifestCanonical", receipt.ManifestCanonical);
            insert.Parameters.AddWithValue("@manifestSignature", receipt.ManifestSignature);
            insert.Parameters.AddWithValue(
                "@releaseTag",
                (object?)receipt.ReleaseTag ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@sourceSha",
                (object?)receipt.SourceSha ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@manifestName",
                (object?)receipt.ManifestName ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@checksumsSha256",
                (object?)receipt.ChecksumsSha256 ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@checksumsSignatureSha256",
                (object?)receipt.ChecksumsSignatureSha256 ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@inventorySha256",
                (object?)receipt.InventorySha256 ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@installReceiptSha256",
                (object?)receipt.InstallReceiptSha256 ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@restartReceiptSha256",
                (object?)receipt.RestartReceiptSha256 ?? DBNull.Value);
            insert.Parameters.AddWithValue("@deviceKeyId", signed.KeyId);
            insert.Parameters.AddWithValue(
                "@receiptJson",
                JsonSerializer.Serialize(receipt, UpdateActivationContract.JsonOptions));
            insert.Parameters.AddWithValue("@deviceSignature", signed.Signature);
            insert.Parameters.AddWithValue("@canonicalDigest", signed.CanonicalDigest);
            insert.Parameters.AddWithValue("@verifiedAtUtc", receipt.VerifiedAtUtc);
            insert.Parameters.AddWithValue("@committedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
            transaction.Commit();
            return new(signed);
        }
    }

    internal PersistedReleaseNoopDeviceReceipt? GetReleaseNoopDeviceReceipt(
        string commandId)
    {
        if (!Guid.TryParseExact(commandId, "D", out _))
            throw new ArgumentException("Release no-op command id is invalid.", nameof(commandId));
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction();
            var existing = ReadReleaseNoopDeviceReceipt(transaction, commandId, null);
            transaction.Commit();
            return existing;
        }
    }

    private PersistedReleaseNoopDeviceReceipt? ReadReleaseNoopDeviceReceipt(
        SqliteTransaction transaction,
        string commandId,
        string? envelopeNonce)
    {
        using var select = CreateCommand(transaction, """
            SELECT command_id, envelope_nonce, data_hash, target_version,
                   ota_signing_key_id, manifest_canonical, manifest_signature,
                   release_tag, source_sha, manifest_name, checksums_sha256,
                   checksums_signature_sha256, inventory_sha256,
                   install_receipt_sha256, restart_receipt_sha256,
                   device_key_id, receipt_json, device_signature, canonical_digest,
                   verified_at_utc
              FROM update_noop_device_receipts
             WHERE command_id = @commandId
                OR (@nonce IS NOT NULL AND envelope_nonce = @nonce)
             LIMIT 1
            """);
        select.Parameters.AddWithValue("@commandId", commandId);
        select.Parameters.AddWithValue("@nonce", (object?)envelopeNonce ?? DBNull.Value);
        using var reader = select.ExecuteReader();
        if (!reader.Read()) return null;

        var storedCommandId = reader.GetString(0);
        var storedNonce = reader.GetString(1);
        var storedDataHash = reader.GetString(2);
        var storedTargetVersion = reader.GetString(3);
        var storedOtaKeyId = reader.GetString(4);
        var storedManifest = reader.GetString(5);
        var storedManifestSignature = reader.GetString(6);
        var storedReleaseTag = reader.IsDBNull(7) ? null : reader.GetString(7);
        var storedSourceSha = reader.IsDBNull(8) ? null : reader.GetString(8);
        var storedManifestName = reader.IsDBNull(9) ? null : reader.GetString(9);
        var storedChecksumsSha256 = reader.IsDBNull(10) ? null : reader.GetString(10);
        var storedChecksumsSignatureSha256 = reader.IsDBNull(11) ? null : reader.GetString(11);
        var storedInventorySha256 = reader.IsDBNull(12) ? null : reader.GetString(12);
        var storedInstallReceiptSha256 = reader.IsDBNull(13) ? null : reader.GetString(13);
        var storedRestartReceiptSha256 = reader.IsDBNull(14) ? null : reader.GetString(14);
        var deviceKeyId = reader.GetString(15);
        var receiptJson = reader.GetString(16);
        var deviceSignature = reader.GetString(17);
        var canonicalDigest = reader.GetString(18);
        var verifiedAtUtc = reader.GetString(19);
        ReleaseNoopDeviceReceipt receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<ReleaseNoopDeviceReceipt>(
                receiptJson,
                UpdateActivationContract.JsonOptions)
                ?? throw new InvalidOperationException("Release no-op receipt is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Release no-op receipt JSON is invalid.", ex);
        }

        if (!string.Equals(storedCommandId, receipt.CommandId, StringComparison.Ordinal) ||
            !string.Equals(storedNonce, receipt.EnvelopeNonce, StringComparison.Ordinal) ||
            !string.Equals(storedDataHash, receipt.CommandDataHash, StringComparison.Ordinal) ||
            !string.Equals(storedTargetVersion, receipt.TargetVersion, StringComparison.Ordinal) ||
            !string.Equals(storedOtaKeyId, receipt.OtaSigningKeyId, StringComparison.Ordinal) ||
            !string.Equals(storedManifest, receipt.ManifestCanonical, StringComparison.Ordinal) ||
            !string.Equals(
                storedManifestSignature,
                receipt.ManifestSignature,
                StringComparison.Ordinal) ||
            !string.Equals(storedReleaseTag, receipt.ReleaseTag, StringComparison.Ordinal) ||
            !string.Equals(storedSourceSha, receipt.SourceSha, StringComparison.Ordinal) ||
            !string.Equals(storedManifestName, receipt.ManifestName, StringComparison.Ordinal) ||
            !string.Equals(
                storedChecksumsSha256,
                receipt.ChecksumsSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                storedChecksumsSignatureSha256,
                receipt.ChecksumsSignatureSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                storedInventorySha256,
                receipt.InventorySha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                storedInstallReceiptSha256,
                receipt.InstallReceiptSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                storedRestartReceiptSha256,
                receipt.RestartReceiptSha256,
                StringComparison.Ordinal) ||
            !string.Equals(verifiedAtUtc, receipt.VerifiedAtUtc, StringComparison.Ordinal) ||
            !IsLowerSha256(deviceKeyId) ||
            !IsLowerSha256(canonicalDigest) ||
            !IsBase64UrlP1363(deviceSignature))
            throw new InvalidOperationException("Release no-op receipt storage binding is invalid.");

        return new(new(receipt, deviceKeyId, deviceSignature, canonicalDigest));
    }

    private void RequireRegisteredUpdateBinding(
        SqliteTransaction transaction,
        ReleaseNoopDeviceReceipt receipt)
    {
        using var select = CreateCommand(transaction, """
            SELECT envelope_nonce, data_hash, target_version
              FROM update_command_receipts
             WHERE command_id = @commandId
             LIMIT 1
            """);
        select.Parameters.AddWithValue("@commandId", receipt.CommandId);
        using var reader = select.ExecuteReader();
        if (!reader.Read() ||
            !string.Equals(reader.GetString(0), receipt.EnvelopeNonce, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), receipt.CommandDataHash, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(2), receipt.TargetVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Release no-op update command binding is missing.");
    }

    private static void ValidateReleaseNoopReceipt(
        ReleaseNoopDeviceReceipt receipt,
        string deviceKeyId)
    {
        var parsedManifest = string.IsNullOrEmpty(receipt.ManifestCanonical)
            ? null
            : UpdateManifest.Parse(receipt.ManifestCanonical);
        var convergenceBindings = new[]
        {
            receipt.ReleaseTag,
            receipt.SourceSha,
            receipt.ManifestName,
            receipt.ChecksumsSha256,
            receipt.ChecksumsSignatureSha256,
            receipt.InventorySha256,
            receipt.InstallReceiptSha256,
            receipt.RestartReceiptSha256,
        };
        var hasConvergenceBindings = convergenceBindings.All(value => value is not null);
        var hasPartialConvergenceBindings = convergenceBindings.Any(value => value is not null) &&
                                            !hasConvergenceBindings;
        if (receipt.SchemaVersion != 1 ||
            !string.Equals(receipt.Purpose, ReleaseNoopPurpose, StringComparison.Ordinal) ||
            !Guid.TryParseExact(receipt.CommandId, "D", out _) ||
            !string.Equals(receipt.Command, UpdateActivationContract.CommandName, StringComparison.Ordinal) ||
            !IsSafeToken(receipt.AgentId, 160) ||
            !IsSafeToken(receipt.MachineFingerprint, 256) ||
            string.IsNullOrWhiteSpace(receipt.CommandTimestamp) ||
            receipt.CommandTimestamp.Length > 64 ||
            receipt.CommandTimestamp.Any(char.IsControl) ||
            !DateTimeOffset.TryParse(
                receipt.CommandTimestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _) ||
            !IsSafeToken(receipt.EnvelopeNonce, 160) ||
            !IsLowerSha256(receipt.CommandDataHash) ||
            !IsSafeToken(receipt.CommandKeyId, 80) ||
            !IsBase64P1363(receipt.CommandSignature) ||
            !IsSafeToken(receipt.TargetVersion, 80) ||
            string.IsNullOrEmpty(receipt.ManifestCanonical) ||
            Encoding.UTF8.GetByteCount(receipt.ManifestCanonical) >
                UpdateActivationContract.MaxRequestBytes ||
            parsedManifest is null ||
            !string.Equals(
                parsedManifest.ToCanonical(),
                receipt.ManifestCanonical,
                StringComparison.Ordinal) ||
            !string.Equals(
                parsedManifest.Version,
                receipt.TargetVersion,
                StringComparison.Ordinal) ||
            receipt.ManifestSignature is not { Length: 128 } ||
            !receipt.ManifestSignature.All(Uri.IsHexDigit) ||
            receipt.OtaSigningKeyId is not (
                OtaUpdateTrust.LegacyV1KeyId or OtaUpdateTrust.CurrentV2KeyId) ||
            !DateTimeOffset.TryParseExact(
                receipt.VerifiedAtUtc,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _) ||
            !IsLowerSha256(deviceKeyId) ||
            hasPartialConvergenceBindings ||
            hasConvergenceBindings && !IsValidConvergenceBinding(receipt))
            throw new InvalidOperationException("Release no-op device receipt is invalid.");
    }

    private static bool IsValidConvergenceBinding(ReleaseNoopDeviceReceipt receipt) =>
        receipt.OtaSigningKeyId == OtaUpdateTrust.LegacyV1KeyId &&
        IsSafeToken(receipt.ReleaseTag, 80) &&
        UpdateActivationContract.VersionsEquivalent(
            receipt.ReleaseTag,
            receipt.TargetVersion) &&
        IsLowerHex(receipt.SourceSha, 40) &&
        string.Equals(
            receipt.ManifestName,
            $"update-manifest-{receipt.ReleaseTag}.txt",
            StringComparison.Ordinal) &&
        IsLowerSha256(receipt.ChecksumsSha256) &&
        IsLowerSha256(receipt.ChecksumsSignatureSha256) &&
        IsLowerSha256(receipt.InventorySha256) &&
        IsLowerSha256(receipt.InstallReceiptSha256) &&
        IsLowerSha256(receipt.RestartReceiptSha256) &&
        receipt.ManifestCanonical.All(char.IsAscii) &&
        receipt.ManifestSignature.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasSameReleaseNoopBinding(
        ReleaseNoopDeviceReceipt existing,
        ReleaseNoopDeviceReceipt candidate) =>
        existing with { VerifiedAtUtc = candidate.VerifiedAtUtc } == candidate;

    private static bool IsLowerSha256(string? value) =>
        IsLowerHex(value, 64);

    private static bool IsLowerHex(string? value, int length) =>
        value is not null && value.Length == length &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSafeToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static bool IsBase64P1363(string? value)
    {
        if (value is not { Length: 88 }) return false;
        try { return Convert.FromBase64String(value).Length == 64; }
        catch (FormatException) { return false; }
    }

    private static bool IsBase64UrlP1363(string? value) =>
        value is { Length: 86 } &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
