using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class SelfUninstallCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-self-uninstall-core-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _maintenanceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private const string KeyId = "test-command-key";
    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string Fingerprint = "fingerprint-self-uninstall-test";
    private const string CommandId = "33333333-3333-4333-8333-333333333333";
    private const string Nonce = "44444444-4444-4444-8444-444444444444";

    public void Dispose()
    {
        _key.Dispose();
        _maintenanceKey.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Terminal_event_is_exported_before_authenticated_request_is_written()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);
        string? uploadedArchive = null;

        var result = await PrepareAsync(
            db,
            requestPath,
            (archive, digest, _) =>
            {
                uploadedArchive = archive;
                return Task.FromResult<SelfUninstallArchiveReceipt?>(CreateReceipt(digest));
            });

        Assert.True(result.IsReady, result.Code);
        Assert.NotNull(uploadedArchive);
        Assert.Contains("\"event_type\":\"self_uninstall\"", uploadedArchive!);
        Assert.Contains("\"to_state\":\"SelfUninstallRequested\"", uploadedArchive!);
        var claimedPath = requestPath + ".claimed";
        Assert.True(File.Exists(claimedPath));
        Assert.Empty(Directory.GetFiles(_root, "uninstall.request.tmp-*"));

        Assert.True(SelfUninstallContract.TryDeserialize(
            await File.ReadAllTextAsync(claimedPath),
            out var request,
            out var deserializeCode), deserializeCode);
        var validation = SelfUninstallContract.Validate(
            request!,
            AgentId,
            Fingerprint,
            Keys,
            _now);
        Assert.True(validation.IsValid, validation.Code);
    }

    [Fact]
    public async Task Missing_archive_ack_blocks_request_write()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);

        var result = await PrepareAsync(
            db,
            requestPath,
            (_, _, _) => Task.FromResult<SelfUninstallArchiveReceipt?>(null));

        Assert.False(result.IsReady);
        Assert.Equal("archive_ack_missing", result.Code);
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task Mismatched_archive_ack_digest_blocks_request_write()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);

        var result = await PrepareAsync(
            db,
            requestPath,
            (_, _, _) => Task.FromResult<SelfUninstallArchiveReceipt?>(
                CreateReceipt(new string('a', 64))));

        Assert.False(result.IsReady);
        Assert.Equal("archive_ack_digest_mismatch", result.Code);
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task Unsigned_archive_ack_blocks_request_write()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);

        var result = await PrepareAsync(
            db,
            requestPath,
            (_, digest, _) => Task.FromResult<SelfUninstallArchiveReceipt?>(
                CreateReceipt(digest) with { Signature = string.Empty }));

        Assert.False(result.IsReady);
        Assert.Equal("archive_receipt_signature_invalid", result.Code);
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task Forged_command_is_rejected_before_audit_export_or_upload()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);
        var uploadCalls = 0;

        var result = await PrepareAsync(
            db,
            requestPath,
            (_, _, _) =>
            {
                uploadCalls++;
                return Task.FromResult<SelfUninstallArchiveReceipt?>(null);
            },
            command => command with
            {
                Signature = Convert.ToBase64String(new byte[64]),
            });

        Assert.False(result.IsReady);
        Assert.Equal("command_signature_invalid", result.Code);
        Assert.Equal(0, uploadCalls);
        Assert.Equal(0, db.GetAuditEntryCount());
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task Authority_expiring_during_archive_upload_blocks_request_write()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);
        var authorityNow = _now;

        var result = await PrepareAsync(
            db,
            requestPath,
            (_, digest, _) =>
            {
                authorityNow = _now.AddMinutes(5);
                return Task.FromResult<SelfUninstallArchiveReceipt?>(
                    CreateReceipt(digest));
            },
            authorityNow: () => authorityNow);

        Assert.False(result.IsReady);
        Assert.Equal("self_uninstall_authority_expired", result.Code);
        Assert.False(File.Exists(requestPath));
    }

    [Fact]
    public async Task Near_expiry_authority_without_broker_handoff_runway_is_not_published()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);
        var authorityNow = _now;

        var result = await PrepareAsync(
            db,
            requestPath,
            (_, digest, _) =>
            {
                authorityNow = _now.AddMinutes(5) -
                    SelfUninstallCoordinator.MinimumBrokerHandoffRunway;
                return Task.FromResult<SelfUninstallArchiveReceipt?>(
                    CreateReceipt(digest));
            },
            authorityNow: () => authorityNow);

        Assert.False(result.IsReady);
        Assert.Equal("self_uninstall_handoff_runway_insufficient", result.Code);
        Assert.False(File.Exists(requestPath));
        Assert.Empty(Directory.GetFiles(_root, "uninstall.request.tmp-*"));
    }

    [Fact]
    public async Task Authority_expiring_at_publish_boundary_removes_temp_request()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);
        var authorityChecks = 0;

        var result = await PrepareAsync(
            db,
            requestPath,
            (_, digest, _) => Task.FromResult<SelfUninstallArchiveReceipt?>(
                CreateReceipt(digest)),
            authorityNow: () => ++authorityChecks < 3
                ? _now
                : _now.AddMinutes(5));

        Assert.False(result.IsReady);
        Assert.Equal("self_uninstall_authority_expired", result.Code);
        Assert.False(File.Exists(requestPath));
        Assert.Empty(Directory.GetFiles(_root, "uninstall.request.tmp-*"));
    }

    [Fact]
    public async Task Transient_archive_failure_can_retry_same_signed_envelope()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);

        var first = await PrepareAsync(
            db,
            requestPath,
            (_, _, _) => throw new HttpRequestException("transient"));

        Assert.False(first.IsReady);
        Assert.Equal("archive_upload_failed", first.Code);
        Assert.False(File.Exists(requestPath));

        var second = await PrepareAsync(
            db,
            requestPath,
            (_, digest, _) => Task.FromResult<SelfUninstallArchiveReceipt?>(
                CreateReceipt(digest)));

        Assert.True(second.IsReady, second.Code);
        Assert.True(File.Exists(requestPath + ".claimed"));
    }

    [Fact]
    public async Task BrokerDowntimeKeepsExactNonceUnconsumedUntilDurableAcceptance()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);
        var uploadCalls = 0;

        var first = await PrepareAsync(
            db,
            requestPath,
            (_, digest, _) =>
            {
                uploadCalls++;
                return Task.FromResult<SelfUninstallArchiveReceipt?>(CreateReceipt(digest));
            },
            autoAccept: false);
        var second = await PrepareAsync(
            db,
            requestPath,
            (_, digest, _) =>
            {
                uploadCalls++;
                return Task.FromResult<SelfUninstallArchiveReceipt?>(CreateReceipt(digest));
            },
            autoAccept: false);

        Assert.Equal("broker_acceptance_pending", first.Code);
        Assert.Equal("broker_acceptance_pending", second.Code);
        Assert.Equal(1, uploadCalls);
        Assert.True(File.Exists(requestPath));
    }

    [Fact]
    public async Task Redelivery_after_publication_reuses_exact_request_without_duplicate_upload_or_audit()
    {
        Directory.CreateDirectory(_root);
        using var db = new AgentStateDb(Path.Combine(_root, "state.db"));
        var requestPath = Path.Combine(_root, SelfUninstallContract.RequestFileName);
        var uploadCalls = 0;
        var exactCommand = CreateCommand();

        Task<SelfUninstallArchiveReceipt?> Upload(
            string _, string digest, CancellationToken __)
        {
            uploadCalls++;
            return Task.FromResult<SelfUninstallArchiveReceipt?>(CreateReceipt(digest));
        }

        var first = await PrepareAsync(
            db, requestPath, Upload, commandOverride: exactCommand);
        var auditCountAfterPublication = db.GetAuditEntryCount();
        var claimedPath = requestPath + ".claimed";
        var exactPublishedBytes = await File.ReadAllTextAsync(claimedPath);
        var second = await PrepareAsync(
            db,
            requestPath,
            Upload,
            authorityNow: () => _now.AddMinutes(10),
            commandOverride: exactCommand);

        Assert.True(first.IsReady, first.Code);
        Assert.True(second.IsReady, second.Code);
        Assert.Equal(1, uploadCalls);
        Assert.Equal(auditCountAfterPublication, db.GetAuditEntryCount());
        Assert.False(File.Exists(requestPath));
        Assert.Equal(exactPublishedBytes, await File.ReadAllTextAsync(claimedPath));
    }

    private async Task<SelfUninstallPreparationResult> PrepareAsync(
        AgentStateDb db,
        string requestPath,
        Func<string, string, CancellationToken, Task<SelfUninstallArchiveReceipt?>> upload,
        Func<SignedCommand, SignedCommand>? mutateCommand = null,
        Func<DateTimeOffset>? authorityNow = null,
        SignedCommand? commandOverride = null,
        bool autoAccept = true)
    {
        var expiresAt = _now.AddMinutes(5);
        var dataJson =
            $"{{\"commandId\":\"{CommandId}\",\"expiresAt\":\"{expiresAt:O}\"}}";
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var timestamp = _now.ToString("O");
        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            SelfUninstallContract.CommandName,
            AgentId,
            Fingerprint,
            timestamp,
            Nonce,
            dataHash);
        var command = commandOverride ?? new SignedCommand(
            SelfUninstallContract.CommandName, AgentId, Fingerprint, timestamp,
            Nonce, KeyId, Sign(canonical), dataHash, expiresAt.ToString("O"));
        command = mutateCommand?.Invoke(command) ?? command;
        var options = new AgentOptions
        {
            AgentId = AgentId,
            MachineFingerprint = Fingerprint,
            PharmacyId = "22222222-2222-4222-8222-222222222222",
            Version = "9.9.9",
            MaintenanceAttestationKeyId = MaintenanceKeyId,
        };

        var first = await SelfUninstallCoordinator.PrepareAsync(
            db,
            options,
            command,
            dataJson,
            CommandId,
            requestPath,
            upload,
            Keys,
            () => _now,
            CancellationToken.None,
            authorityNow);
        if (!autoAccept || first.Code != "broker_acceptance_pending")
            return first;

        var claimedPath = requestPath + ".claimed";
        File.Move(requestPath, claimedPath);
        var exactClaim = await File.ReadAllTextAsync(claimedPath);
        var unsigned = new SelfUninstallBrokerAcceptance(
            SelfUninstallAcceptanceContract.SchemaVersion,
            CommandId,
            Nonce,
            AgentId,
            Fingerprint,
            RemoteCommandTrust.ComputeSha256Hex(exactClaim),
            SelfUninstallAcceptanceContract.FormatTimestamp(_now),
            expiresAt.ToString("O"),
            MaintenanceKeyId,
            MaintenancePublicKeySpki,
            string.Empty);
        var signature = _maintenanceKey.SignData(
            Encoding.UTF8.GetBytes(
                SelfUninstallAcceptanceContract.BuildCanonical(unsigned)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        await File.WriteAllTextAsync(
            SelfUninstallAcceptanceContract.PathForClaim(claimedPath),
            SelfUninstallAcceptanceContract.Serialize(unsigned with
            {
                Signature = SelfUninstallAcceptanceContract.Base64UrlEncode(signature),
            }));
        return await SelfUninstallCoordinator.PrepareAsync(
            db, options, command, dataJson, CommandId, requestPath, upload,
            Keys, () => _now, CancellationToken.None, authorityNow);
    }

    private string MaintenancePublicKeySpki =>
        Convert.ToBase64String(_maintenanceKey.ExportSubjectPublicKeyInfo());

    private string MaintenanceKeyId => Convert.ToHexString(SHA256.HashData(
        _maintenanceKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    private SignedCommand CreateCommand()
    {
        var expiresAt = _now.AddMinutes(5);
        var dataJson =
            $"{{\"commandId\":\"{CommandId}\",\"expiresAt\":\"{expiresAt:O}\"}}";
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var timestamp = _now.ToString("O");
        return new SignedCommand(
            SelfUninstallContract.CommandName,
            AgentId,
            Fingerprint,
            timestamp,
            Nonce,
            KeyId,
            Sign(RemoteCommandTrust.BuildCommandCanonical(
                SelfUninstallContract.CommandName,
                AgentId,
                Fingerprint,
                timestamp,
                Nonce,
                dataHash)),
            dataHash,
            expiresAt.ToString("O"));
    }

    private SelfUninstallArchiveReceipt CreateReceipt(string digest)
    {
        var receipt = new SelfUninstallArchiveReceipt(
            "55555555-5555-4555-8555-555555555555",
            digest,
            _now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            Nonce,
            KeyId,
            string.Empty);
        return receipt with
        {
            Signature = Sign(RemoteCommandTrust.BuildArchiveReceiptCanonical(
                receipt,
                AgentId,
                Fingerprint,
                CommandId,
                Nonce)),
        };
    }

    private IReadOnlyDictionary<string, string> Keys =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyId] = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
        };

    private string Sign(string canonical) => Convert.ToBase64String(
        _key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
}
