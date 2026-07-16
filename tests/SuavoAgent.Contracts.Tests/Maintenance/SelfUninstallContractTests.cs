using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Maintenance;

public sealed class SelfUninstallContractTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private const string KeyId = "test-command-key";
    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string CommandId = "33333333-3333-4333-8333-333333333333";
    private const string OtherCommandId = "33333333-3333-4333-9333-333333333333";
    private const string Nonce = "44444444-4444-4444-8444-444444444444";
    private const string ArchiveId = "55555555-5555-4555-8555-555555555555";

    private IReadOnlyDictionary<string, string> Keys =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyId] = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
        };

    public void Dispose() => _key.Dispose();

    [Fact]
    public void Valid_signed_command_and_signed_archive_receipt_are_accepted()
    {
        var request = CreateRequest();

        var result = SelfUninstallContract.Validate(
            request,
            request.AgentId,
            request.MachineFingerprint,
            Keys,
            _now);

        Assert.True(result.IsValid, result.Code);
    }

    [Theory]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss'Z'")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss.f'Z'")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss.ff'Z'")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss.fff'Z'")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss.ffff'Z'")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss.fffff'Z'")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'")]
    public void Rfc3339_timestamps_used_by_control_plane_are_accepted(string format)
    {
        var request = CreateRequest();
        var timestamp = _now.UtcDateTime.ToString(
            format,
            System.Globalization.CultureInfo.InvariantCulture);
        var receiptTimestamp = _now.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
            System.Globalization.CultureInfo.InvariantCulture);
        var receipt = request.ArchiveReceipt with
        {
            Timestamp = receiptTimestamp,
            Signature = string.Empty,
        };
        receipt = receipt with
        {
            Signature = Sign(RemoteCommandTrust.BuildArchiveReceiptCanonical(
                receipt,
                request.AgentId,
                request.MachineFingerprint,
                request.CommandId,
                request.Nonce)),
        };
        request = request with
        {
            Timestamp = timestamp,
            RequestedAtUtc = timestamp,
            Signature = Sign(RemoteCommandTrust.BuildCommandCanonical(
                request.Command,
                request.AgentId,
                request.MachineFingerprint,
                timestamp,
                request.Nonce,
                request.DataHash)),
            ArchiveReceipt = receipt,
        };

        var result = Validate(request);

        Assert.True(result.IsValid, result.Code);
    }

    [Fact]
    public void Archive_receipt_requires_exact_cloud_millisecond_timestamp()
    {
        var request = CreateRequest();
        var receipt = request.ArchiveReceipt with
        {
            Timestamp = _now.ToString("O"),
            Signature = string.Empty,
        };
        receipt = receipt with
        {
            Signature = Sign(RemoteCommandTrust.BuildArchiveReceiptCanonical(
                receipt,
                request.AgentId,
                request.MachineFingerprint,
                request.CommandId,
                request.Nonce)),
        };

        var result = Validate(request with { ArchiveReceipt = receipt });

        Assert.False(result.IsValid);
        Assert.Equal("archive_receipt_stale", result.Code);
    }

    [Fact]
    public void Non_rfc3339_timestamp_is_rejected()
    {
        var request = CreateRequest();
        var timestamp = _now.UtcDateTime.ToString(
            "MM/dd/yyyy HH:mm:ss 'UTC'",
            System.Globalization.CultureInfo.InvariantCulture);
        request = request with
        {
            Timestamp = timestamp,
            Signature = Sign(RemoteCommandTrust.BuildCommandCanonical(
                request.Command,
                request.AgentId,
                request.MachineFingerprint,
                timestamp,
                request.Nonce,
                request.DataHash)),
        };

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("command_timestamp_invalid_or_stale", result.Code);
    }

    [Fact]
    public void Exact_raw_data_json_survives_request_round_trip()
    {
        var request = CreateRequest(
            dataJson: $"{{ \"commandId\" : \"{CommandId}\", \"expiresAt\" : \"{_now.AddMinutes(4):O}\" }}");

        var json = SelfUninstallContract.Serialize(request);
        Assert.True(SelfUninstallContract.TryDeserialize(json, out var parsed, out var code), code);

        Assert.NotNull(parsed);
        Assert.Equal(request.DataJson, parsed!.DataJson);
        Assert.Equal(request.DataHash, parsed.DataHash);
    }

    [Fact]
    public void Tampered_data_payload_is_rejected_before_launch()
    {
        var request = CreateRequest() with
        {
            DataJson = $"{{\"commandId\":\"{OtherCommandId}\"}}",
        };

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("command_data_hash_mismatch", result.Code);
    }

    [Fact]
    public void Wrong_command_key_is_rejected()
    {
        var request = CreateRequest() with { KeyId = "unknown" };

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("command_signature_invalid", result.Code);
    }

    [Fact]
    public void Stale_signed_command_is_rejected()
    {
        var request = CreateRequest(timestamp: _now.AddMinutes(-10));

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("command_timestamp_invalid_or_stale", result.Code);
    }

    [Fact]
    public void Mismatched_archive_digest_is_rejected()
    {
        var request = CreateRequest() with
        {
            ArchiveDigest = new string('a', 64),
        };

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("archive_digest_mismatch", result.Code);
    }

    [Fact]
    public void Forged_archive_receipt_is_rejected()
    {
        var request = CreateRequest();
        request = request with
        {
            ArchiveReceipt = request.ArchiveReceipt with
            {
                Signature = Convert.ToBase64String(new byte[64]),
            },
        };

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("archive_receipt_signature_invalid", result.Code);
    }

    [Fact]
    public void Unknown_json_fields_fail_closed()
    {
        var json = SelfUninstallContract.Serialize(CreateRequest());
        json = json.Insert(json.Length - 1, ",\"unexpected\":true");

        Assert.False(SelfUninstallContract.TryDeserialize(json, out _, out var code));
        Assert.Equal("request_invalid_json", code);
    }

    [Fact]
    public void Unknown_command_data_fields_are_rejected_as_potential_phi()
    {
        var dataJson =
            $"{{\"commandId\":\"{CommandId}\",\"expiresAt\":\"{_now.AddMinutes(4):O}\",\"patientName\":\"forbidden\"}}";
        var request = CreateRequest(dataJson);

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("payload_command_id_mismatch", result.Code);
    }

    [Fact]
    public void Missing_execution_expiry_is_rejected_at_broker_contract()
    {
        var request = CreateRequest(
            $"{{\"commandId\":\"{CommandId}\"}}");

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("payload_command_id_mismatch", result.Code);
    }

    [Fact]
    public void Expired_execution_authority_is_rejected_at_broker_contract()
    {
        var request = CreateRequest(
            $"{{\"commandId\":\"{CommandId}\",\"expiresAt\":\"{_now.AddSeconds(-1):O}\"}}");

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("command_expiry_invalid_or_stale", result.Code);
    }

    [Fact]
    public void Execution_authority_beyond_five_minutes_is_rejected()
    {
        var request = CreateRequest(
            $"{{\"commandId\":\"{CommandId}\",\"expiresAt\":\"{_now.AddMinutes(5).AddTicks(1):O}\"}}");

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("command_expiry_invalid_or_stale", result.Code);
    }

    private SelfUninstallValidationResult Validate(SelfUninstallRequest request) =>
        SelfUninstallContract.Validate(
            request,
            AgentId,
            "fingerprint-1",
            Keys,
            _now);

    private SelfUninstallRequest CreateRequest(
        string? dataJson = null,
        DateTimeOffset? timestamp = null)
    {
        var commandTimestamp = (timestamp ?? _now).ToString("O");
        dataJson ??=
            $"{{\"commandId\":\"{CommandId}\",\"expiresAt\":\"{_now.AddMinutes(4):O}\"}}";
        const string agentId = AgentId;
        const string fingerprint = "fingerprint-1";
        const string nonce = Nonce;
        const string commandId = CommandId;
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var commandCanonical = RemoteCommandTrust.BuildCommandCanonical(
            SelfUninstallContract.CommandName,
            agentId,
            fingerprint,
            commandTimestamp,
            nonce,
            dataHash);
        var commandSignature = Sign(commandCanonical);
        var digest = RemoteCommandTrust.ComputeSha256Hex("archive-payload");
        var receipt = new SelfUninstallArchiveReceipt(
            ArchiveId,
            digest,
            _now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            nonce,
            KeyId,
            string.Empty);
        receipt = receipt with
        {
            Signature = Sign(RemoteCommandTrust.BuildArchiveReceiptCanonical(
                receipt,
                agentId,
                fingerprint,
                commandId,
                nonce)),
        };

        return new SelfUninstallRequest(
            SelfUninstallContract.SchemaVersion,
            SelfUninstallContract.CommandName,
            agentId,
            fingerprint,
            commandTimestamp,
            nonce,
            KeyId,
            commandSignature,
            dataJson,
            dataHash,
            commandId,
            _now.ToString("O"),
            digest,
            receipt);
    }

    private string Sign(string canonical) => Convert.ToBase64String(
        _key.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
}
