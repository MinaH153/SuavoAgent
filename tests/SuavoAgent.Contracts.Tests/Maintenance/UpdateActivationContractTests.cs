using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Maintenance;

public sealed class UpdateActivationContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 18, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 5)]
    public void Validate_AuthenticTransitionAndFullManifests_ReturnExactDeclaredCohort(
        bool includeMaintenance,
        int expectedFiles)
    {
        using var fixture = RequestFixture.Create(includeMaintenance);

        var result = fixture.Validate();

        Assert.True(result.IsValid, result.Code);
        Assert.Equal(expectedFiles, result.Manifest!.Files.Count);
        Assert.Equal(includeMaintenance, result.Manifest.IncludesMaintenance);
        Assert.Equal(
            includeMaintenance ? MaintenanceContract.ExecutableName : "SuavoAgent.Watchdog.exe",
            result.Manifest.Files[^1].FileName);
    }

    [Fact]
    public void Validate_NineFieldLegacyManifest_IsRejected()
    {
        using var fixture = RequestFixture.Create(includeMaintenance: false, legacyNineField: true);
        Assert.Equal("manifest_field_count_invalid", fixture.Validate().Code);
    }

    [Fact]
    public void Validate_TamperedRawData_IsRejectedBeforeStaging()
    {
        using var fixture = RequestFixture.Create(includeMaintenance: true);
        var tampered = fixture.Request with { DataJson = fixture.Request.DataJson + " " };

        Assert.Equal("command_data_hash_mismatch", fixture.Validate(tampered).Code);
    }

    [Fact]
    public void Validate_NullRawDataFromHostileJson_FailsClosedWithoutThrowing()
    {
        using var fixture = RequestFixture.Create(includeMaintenance: true);
        var malformed = fixture.Request with { DataJson = null! };

        var exception = Record.Exception(() => fixture.Validate(malformed));

        Assert.Null(exception);
        Assert.Equal("command_data_hash_mismatch", fixture.Validate(malformed).Code);
    }

    [Fact]
    public void Validate_RequestManifestNotEqualToSignedCommandData_IsRejected()
    {
        using var fixture = RequestFixture.Create(includeMaintenance: true);
        var tampered = fixture.Request with
        {
            ManifestCanonical = fixture.Request.ManifestCanonical.Replace(
                "v2.0.0",
                "v2.0.1",
                StringComparison.Ordinal),
        };

        Assert.Equal("command_data_manifest_mismatch", fixture.Validate(tampered).Code);
    }

    [Fact]
    public void Validate_StaleSignedCommand_IsRejected()
    {
        using var fixture = RequestFixture.Create(includeMaintenance: true);
        Assert.Equal(
            "command_timestamp_invalid_or_stale",
            fixture.Validate(now: Now.AddMinutes(31)).Code);
    }

    [Fact]
    public void Validate_WrongStagingId_IsRejected()
    {
        using var fixture = RequestFixture.Create(includeMaintenance: true);
        var tampered = fixture.Request with { StagingId = new string('0', 64) };

        Assert.Equal("staging_id_mismatch", fixture.Validate(tampered).Code);
    }

    [Fact]
    public void Validate_DerOrMalformedManifestSignature_IsRejected()
    {
        using var fixture = RequestFixture.Create(includeMaintenance: true);
        var tampered = fixture.Request with { ManifestSignature = "deadbeef" };
        tampered = tampered with
        {
            DataJson = JsonSerializer.Serialize(new
            {
                manifest = tampered.ManifestCanonical,
                manifestSignature = tampered.ManifestSignature,
                channel = "stable",
            }),
        };
        // Rebind the command to the modified data so only the independent update signature fails.
        tampered = fixture.ResignCommandData(tampered);

        Assert.Equal("manifest_signature_invalid", fixture.Validate(tampered).Code);
    }

    [Fact]
    public void TryDeserialize_UnknownField_IsRejected()
    {
        using var fixture = RequestFixture.Create(includeMaintenance: true);
        var json = UpdateActivationContract.Serialize(fixture.Request);
        json = json.Insert(json.Length - 1, ",\"unexpected\":true");

        Assert.False(UpdateActivationContract.TryDeserialize(json, out _, out _));
    }

    private sealed class RequestFixture : IDisposable
    {
        private readonly ECDsa _commandKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa _updateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        private RequestFixture(bool includeMaintenance, bool legacyNineField)
        {
            CommandPublicKey = Convert.ToBase64String(_commandKey.ExportSubjectPublicKeyInfo());
            UpdatePublicKey = Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo());
            var manifest = BuildManifest(includeMaintenance, legacyNineField);
            var manifestSignature = SignHex(_updateKey, manifest);
            var dataJson = JsonSerializer.Serialize(new
            {
                manifest,
                manifestSignature,
                channel = "stable",
            });
            var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
            const string nonce = "nonce-update-0001";
            const string keyId = "test-command-key";
            var commandSignature = SignBase64(
                _commandKey,
                RemoteCommandTrust.BuildCommandCanonical(
                    UpdateActivationContract.CommandName,
                    "agent-0001",
                    "fingerprint-0001",
                    Now.ToString("O"),
                    nonce,
                    dataHash));

            Request = new UpdateActivationRequest(
                UpdateActivationContract.SchemaVersion,
                UpdateActivationContract.CommandName,
                "agent-0001",
                "fingerprint-0001",
                Now.ToString("O"),
                nonce,
                keyId,
                commandSignature,
                dataJson,
                dataHash,
                manifest,
                manifestSignature,
                UpdateActivationContract.ComputeStagingId(nonce, dataHash),
                Now.ToString("O"));
        }

        public UpdateActivationRequest Request { get; private set; }
        public string CommandPublicKey { get; }
        public string UpdatePublicKey { get; }

        public static RequestFixture Create(bool includeMaintenance, bool legacyNineField = false) =>
            new(includeMaintenance, legacyNineField);

        public UpdateActivationValidationResult Validate(
            UpdateActivationRequest? request = null,
            DateTimeOffset? now = null) =>
            UpdateActivationContract.Validate(
                request ?? Request,
                new Dictionary<string, string> { [Request.KeyId] = CommandPublicKey },
                UpdatePublicKey,
                now ?? Now,
                Request.AgentId,
                Request.MachineFingerprint);

        public UpdateActivationRequest ResignCommandData(UpdateActivationRequest request)
        {
            var dataHash = RemoteCommandTrust.ComputeSha256Hex(request.DataJson);
            return request with
            {
                DataHash = dataHash,
                StagingId = UpdateActivationContract.ComputeStagingId(request.Nonce, dataHash),
                Signature = SignBase64(
                    _commandKey,
                    RemoteCommandTrust.BuildCommandCanonical(
                        request.Command,
                        request.AgentId,
                        request.MachineFingerprint,
                        request.Timestamp,
                        request.Nonce,
                        dataHash)),
            };
        }

        public void Dispose()
        {
            _commandKey.Dispose();
            _updateKey.Dispose();
        }

        private static string BuildManifest(bool includeMaintenance, bool legacyNineField)
        {
            const string root = "https://github.com/SuavoLLC/MKM/releases/download/v2.0.0/";
            var core = $"{root}SuavoAgent.Core.exe|{new string('1', 64)}|" +
                       $"{root}SuavoAgent.Broker.exe|{new string('2', 64)}|" +
                       $"{root}SuavoAgent.Helper.exe|{new string('3', 64)}|" +
                       "v2.0.0|net8.0|win-x64";
            if (legacyNineField) return core;
            var transition = $"{core}|{root}SuavoAgent.Watchdog.exe|{new string('4', 64)}";
            return includeMaintenance
                ? $"{transition}|{root}{MaintenanceContract.SignedSetupArtifactName}|{new string('5', 64)}"
                : transition;
        }

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
    }
}
