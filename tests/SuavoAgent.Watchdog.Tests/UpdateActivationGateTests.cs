using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Watchdog;
using Xunit;

namespace SuavoAgent.Watchdog.Tests;

public sealed class UpdateActivationGateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 20, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-watchdog-gate-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _commandKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _updateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public UpdateActivationGateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _commandKey.Dispose();
        _updateKey.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_AuthenticCompleteStaging_AcceptsTransitionAndFullCohorts(bool maintenance)
    {
        var fixture = CreateFixture(maintenance);

        var result = fixture.Gate.Validate(
            fixture.RequestPath,
            fixture.UpdateRoot,
            fixture.Ledger,
            fixture.Request.AgentId,
            fixture.Request.MachineFingerprint,
            "1.9.0",
            Now);

        Assert.True(result.IsValid, result.Code);
        Assert.Equal(maintenance ? 5 : 4, result.Manifest!.Files.Count);
    }

    [Fact]
    public void Validate_ForgedCommandSignature_IsRejected()
    {
        var fixture = CreateFixture(maintenance: true);
        WriteRequest(fixture.RequestPath, fixture.Request with { Signature = Convert.ToBase64String(new byte[64]) });

        Assert.Equal("command_signature_invalid", Validate(fixture).Code);
    }

    [Fact]
    public void Validate_ReplayedReservation_IsRejectedAcrossLedgerInstances()
    {
        var fixture = CreateFixture(maintenance: true);
        var first = Validate(fixture);
        Assert.True(first.IsValid, first.Code);
        Assert.True(fixture.Ledger.TryReserve(first.ReplayId!, Now));

        var freshLedger = new UpdateReplayLedger(fixture.LedgerPath);
        var replay = fixture.Gate.Validate(
            fixture.RequestPath,
            fixture.UpdateRoot,
            freshLedger,
            fixture.Request.AgentId,
            fixture.Request.MachineFingerprint,
            "1.9.0",
            Now);

        Assert.Equal("request_replay", replay.Code);
    }

    [Fact]
    public void Validate_StagedBinaryChangesBetweenPasses_IsRejectedAsToctou()
    {
        var fixture = CreateFixture(maintenance: true);
        var staging = UpdateActivationContract.GetIncomingStagingDirectory(
            fixture.UpdateRoot,
            fixture.Request.StagingId);

        var result = fixture.Gate.Validate(
            fixture.RequestPath,
            fixture.UpdateRoot,
            fixture.Ledger,
            fixture.Request.AgentId,
            fixture.Request.MachineFingerprint,
            "1.9.0",
            Now,
            () => File.WriteAllText(Path.Combine(staging, "SuavoAgent.Core.exe"), "raced"));

        Assert.Equal("staging_toctou_detected", result.Code);
    }

    [Fact]
    public void Validate_RequestChangesAfterVerification_IsRejectedAsToctou()
    {
        var fixture = CreateFixture(maintenance: true);

        var result = fixture.Gate.Validate(
            fixture.RequestPath,
            fixture.UpdateRoot,
            fixture.Ledger,
            fixture.Request.AgentId,
            fixture.Request.MachineFingerprint,
            "1.9.0",
            Now,
            () => File.AppendAllText(fixture.RequestPath, " "));

        Assert.Equal("request_toctou_detected", result.Code);
    }

    [Fact]
    public void Validate_ExtraUntrustedStagedFile_IsRejected()
    {
        var fixture = CreateFixture(maintenance: false);
        var staging = UpdateActivationContract.GetIncomingStagingDirectory(
            fixture.UpdateRoot,
            fixture.Request.StagingId);
        File.WriteAllText(Path.Combine(staging, "payload.cmd"), "not executable");

        Assert.Equal("staging_file_set_mismatch", Validate(fixture).Code);
    }

    [Theory]
    [InlineData("2.0.0")]
    [InlineData("2.1.0")]
    [InlineData("invalid")]
    public void Validate_SameOlderOrUnparseableVersion_IsRejected(string currentVersion)
    {
        var fixture = CreateFixture(maintenance: true);
        var result = fixture.Gate.Validate(
            fixture.RequestPath,
            fixture.UpdateRoot,
            fixture.Ledger,
            fixture.Request.AgentId,
            fixture.Request.MachineFingerprint,
            currentVersion,
            Now);

        Assert.Equal("version_not_strictly_newer", result.Code);
    }

    [Fact]
    public void ReplayLedger_ReleaseAllowsRetryAfterDetachedLaunchFailure()
    {
        var fixture = CreateFixture(maintenance: true);
        var result = Validate(fixture);
        Assert.True(fixture.Ledger.TryReserve(result.ReplayId!, Now));
        fixture.Ledger.Release(result.ReplayId!);

        Assert.False(fixture.Ledger.Contains(result.ReplayId!, Now));
        Assert.True(fixture.Ledger.TryReserve(result.ReplayId!, Now));
    }

    [Fact]
    public void ReplayLedger_ExpiredLaunchLeaseAllowsCrashRecoveryRetry()
    {
        var fixture = CreateFixture(maintenance: true);
        var result = Validate(fixture);
        Assert.True(fixture.Ledger.TryReserve(result.ReplayId!, Now));

        Assert.True(fixture.Ledger.Contains(
            result.ReplayId!,
            Now + UpdateReplayLedger.LaunchLease - TimeSpan.FromMilliseconds(1)));
        Assert.False(fixture.Ledger.Contains(
            result.ReplayId!,
            Now + UpdateReplayLedger.LaunchLease));
        Assert.True(fixture.Ledger.TryReserve(
            result.ReplayId!,
            Now + UpdateReplayLedger.LaunchLease));
    }

    private UpdateActivationGateResult Validate(Fixture fixture) => fixture.Gate.Validate(
        fixture.RequestPath,
        fixture.UpdateRoot,
        fixture.Ledger,
        fixture.Request.AgentId,
        fixture.Request.MachineFingerprint,
        "1.9.0",
        Now);

    private Fixture CreateFixture(bool maintenance)
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
        var manifest = $"{urlRoot}SuavoAgent.Core.exe|{Hash(payloads["SuavoAgent.Core.exe"])}|" +
                       $"{urlRoot}SuavoAgent.Broker.exe|{Hash(payloads["SuavoAgent.Broker.exe"])}|" +
                       $"{urlRoot}SuavoAgent.Helper.exe|{Hash(payloads["SuavoAgent.Helper.exe"])}|" +
                       $"2.0.0|net8.0|win-x64|" +
                       $"{urlRoot}SuavoAgent.Watchdog.exe|{Hash(payloads["SuavoAgent.Watchdog.exe"])}";
        if (maintenance)
            manifest += $"|{urlRoot}{MaintenanceContract.SignedSetupArtifactName}|{Hash(payloads[MaintenanceContract.ExecutableName])}";
        var manifestSignature = SignHex(_updateKey, manifest);
        var dataJson = JsonSerializer.Serialize(new
        {
            manifest,
            manifestSignature,
            channel = "stable",
        });
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        const string nonce = "nonce-gate-0001";
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
            manifest,
            manifestSignature,
            UpdateActivationContract.ComputeStagingId(nonce, dataHash),
            Now.ToString("O"));

        var updateRoot = Path.Combine(_root, "updates-" + Guid.NewGuid().ToString("N"));
        var staging = UpdateActivationContract.GetIncomingStagingDirectory(updateRoot, request.StagingId);
        Directory.CreateDirectory(staging);
        var validation = UpdateActivationContract.ValidateManifest(
            manifest,
            manifestSignature,
            Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo()));
        Assert.True(validation.IsValid, validation.Code);
        foreach (var file in validation.Manifest!.Files)
            File.WriteAllBytes(Path.Combine(staging, file.FileName), payloads[file.FileName]);

        var requestPath = Path.Combine(updateRoot, UpdateActivationContract.ActivationRequestFileName);
        WriteRequest(requestPath, request);
        var ledgerPath = Path.Combine(
            updateRoot,
            UpdateActivationContract.CoordinatorDirectoryName,
            UpdateActivationContract.ReplayLedgerFileName);
        var commandKeys = new Dictionary<string, string>
        {
            [keyId] = Convert.ToBase64String(_commandKey.ExportSubjectPublicKeyInfo()),
        };
        var gate = new UpdateActivationGate(
            commandKeys,
            Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo()),
            NullLogger.Instance);
        return new Fixture(
            updateRoot,
            requestPath,
            ledgerPath,
            request,
            gate,
            new UpdateReplayLedger(ledgerPath));
    }

    private static void WriteRequest(string path, UpdateActivationRequest request)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, UpdateActivationContract.Serialize(request), new UTF8Encoding(false));
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

    private sealed record Fixture(
        string UpdateRoot,
        string RequestPath,
        string LedgerPath,
        UpdateActivationRequest Request,
        UpdateActivationGate Gate,
        UpdateReplayLedger Ledger);
}
