using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class Release1ConvergenceCoordinatorTests : IDisposable
{
    private const string Fingerprint = "release1-convergence-test-host";
    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string ExactUpdateCommandId =
        "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string HistoricalUpdateCommandId =
        "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string ReleaseTag = "v4.0.0";
    private const string SourceSha =
        "2222222222222222222222222222222222222222";
    private const string InventorySha256 =
        "3333333333333333333333333333333333333333333333333333333333333333";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-release1-core-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now =
        DateTimeOffset.Parse("2026-07-15T21:00:00Z");
    private readonly ECDsa _maintenanceKey = ECDsa.Create(
        ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _v1Key = ECDsa.Create(
        ECCurve.NamedCurves.nistP256);
    private readonly InMemoryDeviceAttestationKeyProvider _deviceKeys = new();
    private readonly AgentOptions _options;
    private readonly string _receiptPath;

    public Release1ConvergenceCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
        _receiptPath = Path.Combine(
            _root,
            Release1ConvergenceContract.InstallReceiptFileName);
        using var pending = _deviceKeys.OpenOrCreate(Fingerprint);
        var deviceKeyId = pending.Enrollment.KeyId;
        var deviceKeyName = pending.LocalKeyName;
        _deviceKeys.CommitPending(Fingerprint, deviceKeyId);
        _options = new AgentOptions
        {
            AgentId = AgentId,
            MachineFingerprint = Fingerprint,
            Version = "4.0.0",
            DeviceAttestationKeyId = deviceKeyId,
            DeviceAttestationKeyName = deviceKeyName,
            MaintenanceAttestationKeyId = MaintenanceKeyId(),
        };
        WriteInstallEnvelope(CreateInstallEnvelope());
    }

    [Fact]
    public async Task PreChallengeInstallUploadPersistsAndRetriesExactBytesAfterRestart()
    {
        var dbPath = Path.Combine(_root, "retry.db");
        string firstBody;
        using var signer = new DeviceAuthoritySigner(_options, _deviceKeys);
        using (var firstDb = new AgentStateDb(dbPath))
        {
            var unavailable = new RecordingTransport { AcceptInstall = false };
            var first = Coordinator(firstDb, signer, unavailable);

            await first.RetryPendingAsync(CancellationToken.None);

            firstBody = Assert.Single(unavailable.InstallBodies);
            Assert.Empty(unavailable.AckBodies);
            Assert.Empty(unavailable.PreliminaryBodies);
        }

        using (var restartedDb = new AgentStateDb(dbPath))
        {
            var recovered = new RecordingTransport();
            var restarted = Coordinator(restartedDb, signer, recovered);

            await restarted.RetryPendingAsync(CancellationToken.None);
            await restarted.RetryPendingAsync(CancellationToken.None);

            Assert.Equal(firstBody, Assert.Single(recovered.InstallBodies));
            Assert.Empty(recovered.AckBodies);
            Assert.Empty(recovered.PreliminaryBodies);
        }
    }

    [Theory]
    [InlineData("burn", 64)]
    [InlineData("msi", 63)]
    public void LocalVerifierRejectsUnprovenInstallerTypeOrMalformedMsiDigest(
        string installerType,
        int installerDigestLength)
    {
        var envelope = CreateInstallEnvelope(
            installerType,
            new string('a', installerDigestLength));
        WriteInstallEnvelope(envelope);

        Assert.ThrowsAny<Exception>(() =>
            Release1InstallReceiptVerifier.ReadAndVerifyLocal(
                _receiptPath,
                _options,
                _now));
    }

    [Fact]
    public void LocalVerifierRejectsMaintenanceSignatureTampering()
    {
        var envelope = CreateInstallEnvelope() with
        {
            InstallReceiptSignatureBase64Url = new string('A', 86),
        };
        WriteInstallEnvelope(envelope);

        Assert.ThrowsAny<CryptographicException>(() =>
            Release1InstallReceiptVerifier.ReadAndVerifyLocal(
                _receiptPath,
                _options,
                _now));
    }

    [Fact]
    public void LocalVerifierRejectsNullRequiredBindingAsInvalidData()
    {
        var original = CreateInstallEnvelope();
        var envelope = original with
        {
            InstallReceipt = original.InstallReceipt with { HostDigest = null! },
        };
        WriteInstallEnvelope(envelope);

        Assert.Throws<InvalidDataException>(() =>
            Release1InstallReceiptVerifier.ReadAndVerifyLocal(
                _receiptPath,
                _options,
                _now));
    }

    [Fact]
    public void LocalVerifierRejectsMissingReceiptPayloadAsInvalidData()
    {
        var envelope = CreateInstallEnvelope() with { InstallReceipt = null! };
        WriteInstallEnvelope(envelope);

        Assert.Throws<InvalidDataException>(() =>
            Release1InstallReceiptVerifier.ReadAndVerifyLocal(
                _receiptPath,
                _options,
                _now));
    }

    [Fact]
    public async Task FinalUsesOnlyPreliminaryResponseCommandDespiteMatchingHistory()
    {
        var dbPath = Path.Combine(_root, "exact-command.db");
        using var db = new AgentStateDb(dbPath);
        using var signer = new DeviceAuthoritySigner(_options, _deviceKeys);
        var transport = new RecordingTransport
        {
            PreliminaryCommandId = ExactUpdateCommandId,
        };
        var coordinator = Coordinator(db, signer, transport);
        await coordinator.RetryPendingAsync(CancellationToken.None);
        var challenge = Challenge();

        Assert.True(await coordinator.RegisterAndRetryAsync(
            challenge,
            CancellationToken.None));
        var preliminary = Assert.IsType<PersistedRelease1Preliminary>(
            db.GetRelease1Preliminary(challenge.CommandId));
        Assert.NotNull(db.GetRelease1Delivery(challenge.CommandId, "preliminary"));
        Assert.Null(db.GetRelease1Final(challenge.CommandId));
        var strictGuard = new AgentOptions { StrictOutboundTokenAllowlist = true };
        OutboundPhiGuard.AssertAllowed(
            "/api/agent/release1/install-receipt",
            Assert.Single(transport.InstallBodies),
            strictGuard);
        OutboundPhiGuard.AssertAllowed(
            "/api/agent/release1/preliminary",
            preliminary.RequestJson,
            strictGuard);

        // Both receipts bind the same eight campaign values. The historical
        // receipt is inserted first, but only the command ID returned by the
        // preliminary endpoint is authoritative for final assembly.
        PersistNoop(
            db,
            signer,
            preliminary,
            HistoricalUpdateCommandId,
            "44444444-4444-4444-8444-444444444444");
        PersistNoop(
            db,
            signer,
            preliminary,
            ExactUpdateCommandId,
            "55555555-5555-4555-8555-555555555555");

        await coordinator.RetryPendingAsync(CancellationToken.None);

        var final = Assert.IsType<PersistedRelease1Final>(
            db.GetRelease1Final(challenge.CommandId));
        Assert.Equal(ExactUpdateCommandId, final.NoopCommandId);
        Assert.NotNull(db.GetRelease1Delivery(challenge.CommandId, "final"));
        Assert.Single(transport.FinalBodies);
        OutboundPhiGuard.AssertAllowed(
            "/api/agent/release1/convergence",
            final.RequestJson,
            strictGuard);
        Assert.Equal(
            InventorySha256,
            final.Request.Attestation.InventorySha256);
        Assert.Equal(
            OtaUpdateTrust.LegacyV1KeyId,
            final.Request.Attestation.V1NoopRehearsalReceipt.OtaSigningKeyId);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        AssertAppendOnly(connection,
            "UPDATE release1_install_receipt_uploads SET created_at_utc = created_at_utc");
        AssertAppendOnly(connection,
            "DELETE FROM release1_install_receipt_deliveries");
        AssertAppendOnly(connection,
            "UPDATE release1_convergence_challenges SET registered_at_utc = registered_at_utc");
        AssertAppendOnly(connection,
            "DELETE FROM release1_convergence_preliminary_proofs");
        AssertAppendOnly(connection,
            "UPDATE release1_convergence_final_evidence SET created_at_utc = created_at_utc");
        AssertAppendOnly(connection,
            "DELETE FROM release1_convergence_deliveries");
    }

    [Fact]
    public async Task ExactResponseNoopWithoutConvergenceBindingsFailsClosed()
    {
        var dbPath = Path.Combine(_root, "unbound-noop.db");
        using var db = new AgentStateDb(dbPath);
        using var signer = new DeviceAuthoritySigner(_options, _deviceKeys);
        var transport = new RecordingTransport
        {
            PreliminaryCommandId = ExactUpdateCommandId,
        };
        var coordinator = Coordinator(db, signer, transport);
        var challenge = Challenge();

        Assert.True(await coordinator.RegisterAndRetryAsync(
            challenge,
            CancellationToken.None));
        var preliminary = Assert.IsType<PersistedRelease1Preliminary>(
            db.GetRelease1Preliminary(challenge.CommandId));
        PersistNoop(
            db,
            signer,
            preliminary,
            ExactUpdateCommandId,
            "55555555-5555-4555-8555-555555555555",
            includeConvergenceBindings: false);

        var failure = await Record.ExceptionAsync(() =>
            coordinator.RetryPendingAsync(CancellationToken.None));

        Assert.Null(failure);
        Assert.Null(db.GetRelease1Final(challenge.CommandId));
        Assert.Empty(transport.FinalBodies);
        Assert.Null(db.GetRelease1Delivery(challenge.CommandId, "final"));
    }

    [Fact]
    public async Task CoordinatorCopiesInjectedOtaRootRegistry()
    {
        var dbPath = Path.Combine(_root, "copied-roots.db");
        using var db = new AgentStateDb(dbPath);
        using var signer = new DeviceAuthoritySigner(_options, _deviceKeys);
        var transport = new RecordingTransport
        {
            PreliminaryCommandId = ExactUpdateCommandId,
        };
        var mutableRoots = TestOtaRoots();
        var coordinator = Coordinator(db, signer, transport, mutableRoots);
        mutableRoots.Clear();
        var challenge = Challenge();

        Assert.True(await coordinator.RegisterAndRetryAsync(
            challenge,
            CancellationToken.None));
        var preliminary = Assert.IsType<PersistedRelease1Preliminary>(
            db.GetRelease1Preliminary(challenge.CommandId));
        PersistNoop(
            db,
            signer,
            preliminary,
            ExactUpdateCommandId,
            "55555555-5555-4555-8555-555555555555");

        await coordinator.RetryPendingAsync(CancellationToken.None);

        Assert.NotNull(db.GetRelease1Final(challenge.CommandId));
        Assert.Single(transport.FinalBodies);
    }

    private Release1ConvergenceCoordinator Coordinator(
        AgentStateDb db,
        IDeviceAuthoritySigner signer,
        RecordingTransport transport,
        IReadOnlyDictionary<string, string>? otaRoots = null) => new(
        db,
        _options,
        signer,
        transport,
        NullLogger.Instance,
        _receiptPath,
        () => _now,
        () => new string('e', 64),
        otaRoots ?? TestOtaRoots());

    private Dictionary<string, string> TestOtaRoots() =>
        new(StringComparer.Ordinal)
        {
            [OtaUpdateTrust.LegacyV1KeyId] = Convert.ToBase64String(
                _v1Key.ExportSubjectPublicKeyInfo()),
        };

    private Release1ConvergenceChallenge Challenge()
    {
        var commandId = "66666666-6666-4666-8666-666666666666";
        var expiresAt = Release1ConvergenceContract.ExactUtc(_now.AddHours(1));
        return new(
            commandId,
            InventorySha256,
            ReleaseTag,
            SourceSha,
            expiresAt,
            new SignedCommand(
                Release1ConvergenceCommand.Name,
                AgentId,
                Fingerprint,
                _now.ToString("O"),
                "77777777-7777-4777-8777-777777777777",
                "suavo-cmd-v1",
                Convert.ToBase64String(new byte[64]),
                new string('8', 64),
                expiresAt));
    }

    private void PersistNoop(
        AgentStateDb db,
        IDeviceAuthoritySigner signer,
        PersistedRelease1Preliminary preliminary,
        string commandId,
        string nonce,
        bool includeConvergenceBindings = true)
    {
        var manifest = Manifest();
        var signature = Convert.ToHexString(_v1Key.SignData(
            Encoding.UTF8.GetBytes(manifest),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            .ToLowerInvariant();
        var dataHash = new string(commandId[0], 64);
        Assert.True(db.RegisterUpdateCommandReceipt(
            commandId,
            nonce,
            dataHash,
            "4.0.0").Accepted);
        db.GetOrCreateReleaseNoopDeviceReceipt(
            new ReleaseNoopDeviceReceipt(
                1,
                AgentStateDb.ReleaseNoopPurpose,
                commandId,
                UpdateActivationContract.CommandName,
                AgentId,
                Fingerprint,
                _now.ToString("O"),
                nonce,
                dataHash,
                "suavo-cmd-v1",
                Convert.ToBase64String(new byte[64]),
                "4.0.0",
                manifest,
                signature,
                OtaUpdateTrust.LegacyV1KeyId,
                includeConvergenceBindings ? ReleaseTag : null,
                includeConvergenceBindings ? SourceSha : null,
                includeConvergenceBindings
                    ? $"update-manifest-{ReleaseTag}.txt"
                    : null,
                includeConvergenceBindings
                    ? preliminary.Request.Proof.InstallReceipt.ChecksumsSha256
                    : null,
                includeConvergenceBindings
                    ? preliminary.Request.Proof.InstallReceipt.ChecksumsSignatureSha256
                    : null,
                includeConvergenceBindings ? InventorySha256 : null,
                includeConvergenceBindings ? preliminary.InstallReceiptSha256 : null,
                includeConvergenceBindings ? preliminary.RestartReceiptSha256 : null,
                _now.AddSeconds(1).ToString("O")),
            signer);
    }

    private SignedRelease1InstallReceipt CreateInstallEnvelope(
        string installerType = "msi",
        string? installerDigest = null)
    {
        var receipt = new Release1InstallReceipt(
            Release1ConvergenceContract.ReceiptSchemaVersion,
            Release1ConvergenceContract.InstallReceiptPurpose,
            Release1ConvergenceContract.HostDigest(Fingerprint),
            MaintenanceKeyId(),
            ReleaseTag,
            SourceSha,
            installerType,
            installerDigest ?? new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SuavoAgent.Core.exe"] = new string('1', 64),
                ["SuavoAgent.Broker.exe"] = new string('2', 64),
                ["SuavoAgent.Helper.exe"] = new string('3', 64),
                ["SuavoAgent.Watchdog.exe"] = new string('4', 64),
                [MaintenanceContract.SignedSetupArtifactName] = new string('5', 64),
            },
            new string('c', 64),
            Release1ConvergenceContract.ExactUtc(_now.AddMinutes(-5)),
            new string('d', 64),
            Release1ConvergenceContract.FullReinstallMode);
        var signature = _maintenanceKey.SignData(
            Release1ConvergenceContract.CanonicalBytes(receipt),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new(
            receipt,
            Release1ConvergenceContract.Base64Url(signature),
            Convert.ToBase64String(_maintenanceKey.ExportSubjectPublicKeyInfo()));
    }

    private string MaintenanceKeyId() => Convert.ToHexString(SHA256.HashData(
        _maintenanceKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    private static void AssertAppendOnly(
        SqliteConnection connection,
        string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var error = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Contains("append_only", error.Message, StringComparison.Ordinal);
    }

    private void WriteInstallEnvelope(SignedRelease1InstallReceipt envelope) =>
        File.WriteAllBytes(
            _receiptPath,
            Release1ConvergenceContract.CanonicalBytes(envelope));

    private static string Manifest()
    {
        var digest = new string('a', 64);
        const string root =
            "https://github.com/SuavoLLC/MKM/releases/download/v4.0.0";
        return $"{root}/SuavoAgent.Core.exe|{digest}|" +
               $"{root}/SuavoAgent.Broker.exe|{digest}|" +
               $"{root}/SuavoAgent.Helper.exe|{digest}|" +
               $"4.0.0|net8.0|win-x64|" +
               $"{root}/SuavoAgent.Watchdog.exe|{digest}";
    }

    public void Dispose()
    {
        _maintenanceKey.Dispose();
        _v1Key.Dispose();
        _deviceKeys.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private sealed class RecordingTransport : IRelease1ConvergenceTransport
    {
        internal bool AcceptInstall { get; init; } = true;
        internal string PreliminaryCommandId { get; init; } =
            "99999999-9999-4999-8999-999999999999";
        internal List<string> InstallBodies { get; } = [];
        internal List<string> AckBodies { get; } = [];
        internal List<string> PreliminaryBodies { get; } = [];
        internal List<string> FinalBodies { get; } = [];

        public Task<bool> SendInstallReceiptAsync(
            string exactRequestJson,
            CancellationToken cancellationToken)
        {
            InstallBodies.Add(exactRequestJson);
            return Task.FromResult(AcceptInstall);
        }

        public Task<bool> AckChallengeAsync(
            string commandId,
            string exactRequestJson,
            CancellationToken cancellationToken)
        {
            AckBodies.Add(exactRequestJson);
            return Task.FromResult(true);
        }

        public Task<string?> SendPreliminaryAsync(
            string exactRequestJson,
            CancellationToken cancellationToken)
        {
            PreliminaryBodies.Add(exactRequestJson);
            return Task.FromResult<string?>(PreliminaryCommandId);
        }

        public Task<bool> SendFinalAsync(
            string exactRequestJson,
            CancellationToken cancellationToken)
        {
            FinalBodies.Add(exactRequestJson);
            return Task.FromResult(true);
        }
    }
}
