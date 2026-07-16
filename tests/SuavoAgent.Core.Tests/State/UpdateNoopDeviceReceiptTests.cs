using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class UpdateNoopDeviceReceiptTests : IDisposable
{
    private const string CommandId = "11111111-1111-4111-8111-111111111111";
    private const string Nonce = "22222222-2222-4222-8222-222222222222";
    private const string DataHash =
        "3333333333333333333333333333333333333333333333333333333333333333";
    private const string Fingerprint = "release-noop-device-test";
    private const string Version = "3.9.2";

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"suavo-update-noop-{Guid.NewGuid():N}.db");
    private readonly InMemoryDeviceAttestationKeyProvider _keys = new();
    private readonly AgentOptions _options = new()
    {
        AgentId = "44444444-4444-4444-8444-444444444444",
        MachineFingerprint = Fingerprint,
    };
    private readonly string _publicKey;

    public UpdateNoopDeviceReceiptTests()
    {
        using var pending = _keys.OpenOrCreate(Fingerprint);
        _publicKey = pending.Enrollment.PublicKeySpki;
        _keys.CommitPending(Fingerprint, pending.Enrollment.KeyId);
    }

    [Fact]
    public void ExactRetryReturnsOriginalDeviceSignedBytesAndSurvivesRestart()
    {
        AgentStateDb.PersistedReleaseNoopDeviceReceipt first;
        using (var db = new AgentStateDb(_path))
        using (var signer = new DeviceAuthoritySigner(_options, _keys))
        {
            Assert.True(db.RegisterUpdateCommandReceipt(
                CommandId,
                Nonce,
                DataHash,
                Version).Accepted);
            var receipt = Receipt();
            first = db.GetOrCreateReleaseNoopDeviceReceipt(receipt, signer);
            var retry = db.GetOrCreateReleaseNoopDeviceReceipt(
                receipt with { VerifiedAtUtc = "2026-07-15T12:01:00.0000000Z" },
                signer);
            Assert.Equal(first, retry);
        }

        using var restarted = new AgentStateDb(_path);
        var persisted = restarted.GetReleaseNoopDeviceReceipt(CommandId);
        Assert.NotNull(persisted);
        Assert.Equal(first, persisted);
        Assert.Equal(Manifest(), persisted.Signed.Receipt.ManifestCanonical);
        Assert.Equal(new string('a', 128), persisted.Signed.Receipt.ManifestSignature);
        Assert.Equal(
            OtaUpdateTrust.LegacyV1KeyId,
            persisted.Signed.Receipt.OtaSigningKeyId);

        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(_publicKey), out _);
        var canonical = "suavo.release-noop.v1\n" +
            DeviceAuthorityCanonical.Serialize(persisted.Signed.Receipt);
        Assert.True(verifier.VerifyData(
            Encoding.UTF8.GetBytes(canonical),
            DecodeBase64Url(persisted.Signed.Signature),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void SameCommandWithDifferentExactManifestSignatureFailsClosed()
    {
        using var db = new AgentStateDb(_path);
        using var signer = new DeviceAuthoritySigner(_options, _keys);
        db.RegisterUpdateCommandReceipt(CommandId, Nonce, DataHash, Version);
        db.GetOrCreateReleaseNoopDeviceReceipt(Receipt(), signer);

        Assert.Throws<InvalidOperationException>(() =>
            db.GetOrCreateReleaseNoopDeviceReceipt(
                Receipt() with { ManifestSignature = new string('b', 128) },
                signer));
    }

    [Fact]
    public void ConvergenceBindingRequiresAllEightFieldsAndPersistsExactValues()
    {
        using var db = new AgentStateDb(_path);
        using var signer = new DeviceAuthoritySigner(_options, _keys);
        db.RegisterUpdateCommandReceipt(CommandId, Nonce, DataHash, Version);
        var complete = Receipt() with
        {
            ReleaseTag = "v3.9.2",
            SourceSha = new string('b', 40),
            ManifestName = "update-manifest-v3.9.2.txt",
            ChecksumsSha256 = new string('c', 64),
            ChecksumsSignatureSha256 = new string('d', 64),
            InventorySha256 = new string('e', 64),
            InstallReceiptSha256 = new string('f', 64),
            RestartReceiptSha256 = new string('1', 64),
        };

        Assert.Throws<InvalidOperationException>(() =>
            db.GetOrCreateReleaseNoopDeviceReceipt(
                complete with { RestartReceiptSha256 = null },
                signer));

        var persisted = db.GetOrCreateReleaseNoopDeviceReceipt(complete, signer);
        Assert.Equal("v3.9.2", persisted.Signed.Receipt.ReleaseTag);
        Assert.Equal(new string('b', 40), persisted.Signed.Receipt.SourceSha);
        Assert.Equal(new string('c', 64), persisted.Signed.Receipt.ChecksumsSha256);
        Assert.Equal(new string('1', 64), persisted.Signed.Receipt.RestartReceiptSha256);
    }

    [Fact]
    public void PersistedNoopProofIsAppendOnly()
    {
        using var db = new AgentStateDb(_path);
        using var signer = new DeviceAuthoritySigner(_options, _keys);
        db.RegisterUpdateCommandReceipt(CommandId, Nonce, DataHash, Version);
        db.GetOrCreateReleaseNoopDeviceReceipt(Receipt(), signer);

        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE update_noop_device_receipts
               SET target_version = '9.9.9'
             WHERE command_id = @commandId
            """;
        update.Parameters.AddWithValue("@commandId", CommandId);
        var updateError = Assert.Throws<SqliteException>(() => update.ExecuteNonQuery());
        Assert.Contains("append_only", updateError.Message, StringComparison.Ordinal);

        using var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM update_noop_device_receipts
             WHERE command_id = @commandId
            """;
        delete.Parameters.AddWithValue("@commandId", CommandId);
        var deleteError = Assert.Throws<SqliteException>(() => delete.ExecuteNonQuery());
        Assert.Contains("append_only", deleteError.Message, StringComparison.Ordinal);
    }

    private static ReleaseNoopDeviceReceipt Receipt() => new(
        SchemaVersion: 1,
        Purpose: AgentStateDb.ReleaseNoopPurpose,
        CommandId,
        Command: UpdateActivationContract.CommandName,
        AgentId: "44444444-4444-4444-8444-444444444444",
        MachineFingerprint: Fingerprint,
        CommandTimestamp: "2026-07-15T12:00:00.123Z",
        EnvelopeNonce: Nonce,
        CommandDataHash: DataHash,
        CommandKeyId: "suavo-cmd-v1",
        CommandSignature: Convert.ToBase64String(new byte[64]),
        TargetVersion: Version,
        ManifestCanonical: Manifest(),
        ManifestSignature: new string('a', 128),
        OtaSigningKeyId: OtaUpdateTrust.LegacyV1KeyId,
        ReleaseTag: null,
        SourceSha: null,
        ManifestName: null,
        ChecksumsSha256: null,
        ChecksumsSignatureSha256: null,
        InventorySha256: null,
        InstallReceiptSha256: null,
        RestartReceiptSha256: null,
        VerifiedAtUtc: "2026-07-15T12:00:01.0000000Z");

    private static string Manifest()
    {
        var hash = new string('a', 64);
        const string baseUrl = "https://github.com/SuavoLLC/MKM/releases/download/v3.9.2";
        return $"{baseUrl}/SuavoAgent.Core.exe|{hash}|" +
               $"{baseUrl}/SuavoAgent.Broker.exe|{hash}|" +
               $"{baseUrl}/SuavoAgent.Helper.exe|{hash}|" +
               $"{Version}|net8.0|win-x64|" +
               $"{baseUrl}/SuavoAgent.Watchdog.exe|{hash}";
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    public void Dispose()
    {
        _keys.Dispose();
        try { File.Delete(_path); } catch { }
        try { File.Delete(_path + "-wal"); } catch { }
        try { File.Delete(_path + "-shm"); } catch { }
    }
}
