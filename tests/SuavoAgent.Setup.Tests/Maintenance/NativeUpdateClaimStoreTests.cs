using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class NativeUpdateClaimStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 20, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-native-update-claim-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _commandKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _updateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Signed_source_is_copied_and_reverified_in_system_only_claim(bool includeMaintenance)
    {
        var fixture = CreateFixture(includeMaintenance);
        var result = fixture.Store.Claim(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now);

        Assert.True(result.Succeeded, result.Code);
        Assert.False(result.Claim!.WasAlreadyClaimed);
        Assert.Equal(
            UpdateActivationContract.GetCoordinatorRequestPath(
                fixture.MaintenanceRoot,
                fixture.Request.StagingId),
            result.Claim.RequestPath);
        Assert.True(File.Exists(result.Claim.RequestPath));
        Assert.Equal(fixture.Manifest.Files.Count, Directory.GetFiles(result.Claim.PayloadDirectory).Length);
        Assert.Equal(
            AuthoritativeReplayState.Claimed,
            fixture.Ledger.Find(result.Claim.Validated.ReplayId)!.State);
    }

    [Fact]
    public void Existing_identical_durable_claim_is_idempotently_resumed()
    {
        var fixture = CreateFixture(includeMaintenance: true);
        var first = fixture.Store.Claim(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now);
        var second = fixture.Store.Claim(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now.AddSeconds(5));

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.True(second.Claim!.WasAlreadyClaimed);
        Assert.Equal(first.Claim!.Validated.ReplayId, second.Claim.Validated.ReplayId);
    }

    [Fact]
    public void Tampered_source_never_creates_a_durable_claim_or_replay_entry()
    {
        var fixture = CreateFixture(includeMaintenance: true);
        File.AppendAllText(
            Path.Combine(fixture.PayloadDirectory, "SuavoAgent.Core.exe"),
            "tamper");

        var result = fixture.Store.Claim(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now);

        Assert.False(result.Succeeded);
        Assert.Equal("payload_hash_invalid", result.Code);
        Assert.Empty(Directory.Exists(fixture.MaintenanceRoot)
            ? Directory.GetDirectories(fixture.MaintenanceRoot, "*", SearchOption.AllDirectories)
            : Array.Empty<string>());
    }

    [Fact]
    public void Completed_replay_can_never_be_reactivated()
    {
        var fixture = CreateFixture(includeMaintenance: true);
        var first = fixture.Store.Claim(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now);
        Assert.True(first.Succeeded);
        Assert.True(fixture.Ledger.TryTransition(
            first.Claim!.Validated.ReplayId,
            AuthoritativeReplayState.Claimed,
            AuthoritativeReplayState.Completed,
            Now.AddMinutes(1)));

        var replay = fixture.Store.Claim(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now.AddMinutes(2));

        Assert.False(replay.Succeeded);
        Assert.Equal("authoritative_replay_rejected", replay.Code);
    }

    [Fact]
    public void Wrong_source_path_is_rejected_even_when_bytes_are_signed()
    {
        var fixture = CreateFixture(includeMaintenance: true);
        var relocated = Path.Combine(fixture.UpdateRoot, "relocated.request.json");
        File.Copy(fixture.RequestPath, relocated);

        var result = fixture.Store.Claim(
            relocated,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now);

        Assert.False(result.Succeeded);
        Assert.Equal("source_path_not_fixed", result.Code);
    }

    [Fact]
    public void Active_pointer_heartbeats_then_writes_terminal_receipt_before_disappearing()
    {
        var fixture = CreateFixture(includeMaintenance: true);
        var claimed = fixture.Store.Claim(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now).Claim!;
        var pointers = new UpdateClaimPointerStore(fixture.MaintenanceRoot);

        var pointer = pointers.Begin(claimed, Now);
        pointer = pointers.Heartbeat(pointer, Now.AddSeconds(15));
        pointers.Complete(pointer, "committed", Now, Now.AddSeconds(30));

        Assert.Null(pointers.TryReadPointer(Now.AddSeconds(30)));
        Assert.True(File.Exists(pointers.CompletionPath));
        Assert.True(UpdateActivationContract.TryDeserializeCompletion(
            File.ReadAllText(pointers.CompletionPath),
            out var completion,
            out _));
        Assert.True(UpdateActivationContract.ValidateCompletion(
            completion!,
            pointer,
            Now.AddSeconds(30),
            out var code), code);
    }

    [Fact]
    public void Terminal_receipt_blocks_same_replay_from_becoming_active_again()
    {
        var fixture = CreateFixture(includeMaintenance: true);
        var claimed = fixture.Store.Claim(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now).Claim!;
        var pointers = new UpdateClaimPointerStore(fixture.MaintenanceRoot);
        var pointer = pointers.Begin(claimed, Now);
        pointers.Complete(pointer, "failed", Now, Now.AddSeconds(1));

        Assert.Throws<InvalidOperationException>(() =>
            pointers.Begin(claimed, Now.AddSeconds(2)));
    }

    [Fact]
    public void Initial_claim_requires_fresh_command_but_system_owned_resume_remains_verifiable()
    {
        var fixture = CreateFixture(includeMaintenance: true);
        var afterFreshnessWindow = Now + UpdateActivationContract.MaximumRequestAge + TimeSpan.FromMinutes(1);

        var initial = fixture.Validator.Validate(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            afterFreshnessWindow);
        var durableResume = fixture.Validator.Validate(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            afterFreshnessWindow,
            allowExpiredDurableClaim: true);

        Assert.False(initial.IsValid);
        Assert.True(durableResume.IsValid, durableResume.Code);
        Assert.Equal(
            UpdateActivationContract.ComputeReplayId(fixture.Request),
            durableResume.Claim!.ReplayId);
    }

    [Fact]
    public void Validation_reports_real_hashing_progress_for_each_verification_pass()
    {
        var fixture = CreateFixture(includeMaintenance: true);
        var progressCalls = 0;

        var result = fixture.Validator.Validate(
            fixture.RequestPath,
            fixture.PayloadDirectory,
            fixture.Identity,
            Now,
            progress: () => progressCalls++);

        Assert.True(result.IsValid, result.Code);
        Assert.Equal(fixture.Manifest.Files.Count * 2, progressCalls);
    }

    private Fixture CreateFixture(bool includeMaintenance)
    {
        var payloads = new Dictionary<string, byte[]>
        {
            ["SuavoAgent.Core.exe"] = Encoding.UTF8.GetBytes("core"),
            ["SuavoAgent.Broker.exe"] = Encoding.UTF8.GetBytes("broker"),
            ["SuavoAgent.Helper.exe"] = Encoding.UTF8.GetBytes("helper"),
            ["SuavoAgent.Watchdog.exe"] = Encoding.UTF8.GetBytes("watchdog"),
            [MaintenanceContract.ExecutableName] = Encoding.UTF8.GetBytes("maintenance"),
        };
        const string urlRoot = "https://github.com/SuavoLLC/MKM/releases/download/v2.0.0/";
        var manifestText = $"{urlRoot}SuavoAgent.Core.exe|{Hash(payloads["SuavoAgent.Core.exe"])}|" +
                           $"{urlRoot}SuavoAgent.Broker.exe|{Hash(payloads["SuavoAgent.Broker.exe"])}|" +
                           $"{urlRoot}SuavoAgent.Helper.exe|{Hash(payloads["SuavoAgent.Helper.exe"])}|" +
                           $"2.0.0|net8.0|win-x64|" +
                           $"{urlRoot}SuavoAgent.Watchdog.exe|{Hash(payloads["SuavoAgent.Watchdog.exe"])}";
        if (includeMaintenance)
            manifestText += $"|{urlRoot}{MaintenanceContract.SignedSetupArtifactName}|{Hash(payloads[MaintenanceContract.ExecutableName])}";
        var manifestSignature = SignHex(_updateKey, manifestText);
        var dataJson = JsonSerializer.Serialize(new
        {
            manifest = manifestText,
            manifestSignature,
            channel = "stable",
        });
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        const string nonce = "nonce-claim-0001";
        const string keyId = "test-command-key";
        var request = new UpdateActivationRequest(
            UpdateActivationContract.SchemaVersion,
            UpdateActivationContract.CommandName,
            "agent-0001",
            "fingerprint-0001",
            Now.ToString("O"),
            nonce,
            keyId,
            SignBase64(
                _commandKey,
                RemoteCommandTrust.BuildCommandCanonical(
                    UpdateActivationContract.CommandName,
                    "agent-0001",
                    "fingerprint-0001",
                    Now.ToString("O"),
                    nonce,
                    dataHash)),
            dataJson,
            dataHash,
            manifestText,
            manifestSignature,
            UpdateActivationContract.ComputeStagingId(nonce, dataHash),
            Now.ToString("O"));
        var updateRoot = Path.Combine(_root, "updates-" + Guid.NewGuid().ToString("N"));
        var payloadDirectory = UpdateActivationContract.GetIncomingStagingDirectory(
            updateRoot,
            request.StagingId);
        Directory.CreateDirectory(payloadDirectory);
        var manifest = UpdateActivationContract.ValidateManifest(
            manifestText,
            manifestSignature,
            Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo())).Manifest!;
        foreach (var file in manifest.Files)
            File.WriteAllBytes(Path.Combine(payloadDirectory, file.FileName), payloads[file.FileName]);
        var requestPath = Path.Combine(updateRoot, UpdateActivationContract.ActivationRequestFileName);
        File.WriteAllText(requestPath, UpdateActivationContract.Serialize(request), new UTF8Encoding(false));

        var maintenanceRoot = Path.Combine(_root, "maintenance-" + Guid.NewGuid().ToString("N"));
        var validator = new NativeUpdateClaimValidator(
            new Dictionary<string, string>
            {
                [keyId] = Convert.ToBase64String(_commandKey.ExportSubjectPublicKeyInfo()),
            },
            Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo()));
        var ledger = new AuthoritativeUpdateReplayLedger(
            Path.Combine(maintenanceRoot, UpdateActivationContract.ReplayLedgerFileName));
        var store = new NativeUpdateClaimStore(
            maintenanceRoot,
            validator,
            ledger,
            lockdown: _ => { },
            sourceUpdateRoot: updateRoot);
        return new Fixture(
            updateRoot,
            maintenanceRoot,
            requestPath,
            payloadDirectory,
            request,
            manifest,
            new InstalledUpdateIdentity("agent-0001", "fingerprint-0001", "1.9.0"),
            validator,
            ledger,
            store);
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string SignHex(ECDsa key, string canonical) => Convert.ToHexString(
        key.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static string SignBase64(ECDsa key, string canonical) => Convert.ToBase64String(
        key.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose()
    {
        _commandKey.Dispose();
        _updateKey.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private sealed record Fixture(
        string UpdateRoot,
        string MaintenanceRoot,
        string RequestPath,
        string PayloadDirectory,
        UpdateActivationRequest Request,
        UpdatePackageManifest Manifest,
        InstalledUpdateIdentity Identity,
        NativeUpdateClaimValidator Validator,
        AuthoritativeUpdateReplayLedger Ledger,
        NativeUpdateClaimStore Store);
}
