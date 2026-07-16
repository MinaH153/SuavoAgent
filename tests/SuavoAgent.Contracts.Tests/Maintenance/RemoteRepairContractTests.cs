using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Maintenance;

public sealed class RemoteRepairContractTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 22, 0, 0, TimeSpan.Zero);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private const string KeyId = "test-key";

    private IReadOnlyDictionary<string, string> Keys => new Dictionary<string, string>
    {
        [KeyId] = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()),
    };

    [Theory]
    [InlineData("repair")]
    [InlineData("repair_agent")]
    public void Exact_signed_minimum_necessary_request_is_accepted(string command)
    {
        var request = Create(command: command);

        var result = RemoteRepairContract.Validate(
            request,
            request.AgentId,
            request.MachineFingerprint,
            Keys,
            Now);

        Assert.True(result.IsValid, result.Code);
        Assert.Equal(RemoteRepairContract.ComputeReplayId(request), result.ReplayId);
    }

    [Fact]
    public void Unknown_payload_field_is_rejected_as_potential_phi()
    {
        var request = Create(dataJson:
            "{\"commandId\":\"cmd-1\",\"reason\":\"watchdog_critical\",\"expiresAt\":\"2026-07-10T22:04:00.0000000+00:00\",\"patientName\":\"forbidden\"}");

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("command_data_invalid", result.Code);
    }

    [Fact]
    public void Tampered_reason_or_raw_payload_is_rejected()
    {
        var request = Create();

        Assert.Equal("request_identity_invalid", Validate(request with
        {
            Reason = "patient_john_smith",
        }).Code);
        Assert.Equal("command_data_hash_mismatch", Validate(request with
        {
            DataJson = "{\"commandId\":\"cmd-2\",\"reason\":\"watchdog_critical\"}",
        }).Code);
    }

    [Fact]
    public void Wrong_identity_forged_signature_and_stale_command_fail_closed()
    {
        var request = Create();

        Assert.Equal("agent_mismatch", RemoteRepairContract.Validate(
            request, "other-agent", request.MachineFingerprint, Keys, Now).Code);
        Assert.Equal("command_signature_invalid", Validate(request with
        {
            Signature = Convert.ToBase64String(new byte[64]),
        }).Code);
        Assert.Equal("command_timestamp_invalid_or_stale", Validate(
            Create(timestamp: Now - RemoteRepairContract.MaximumRequestAge - TimeSpan.FromSeconds(1))).Code);
    }

    [Fact]
    public void Unknown_outer_json_field_fails_closed()
    {
        var json = RemoteRepairContract.Serialize(Create());
        json = json.Insert(json.Length - 1, ",\"unexpected\":true");

        Assert.False(RemoteRepairContract.TryDeserialize(json, out _, out var code));
        Assert.Equal("request_invalid_json", code);
    }

    [Fact]
    public void Replay_identity_does_not_depend_on_malleable_signature_bytes()
    {
        var request = Create();

        Assert.Equal(
            RemoteRepairContract.ComputeReplayId(request),
            RemoteRepairContract.ComputeReplayId(request with
            {
                Signature = Convert.ToBase64String(new byte[64]),
            }));
    }

    [Fact]
    public void Missing_execution_expiry_is_rejected()
    {
        var request = Create(dataJson:
            "{\"commandId\":\"cmd-1\",\"reason\":\"watchdog_critical\"}");

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("command_data_invalid", result.Code);
    }

    [Fact]
    public void Expired_execution_authority_is_rejected_by_watchdog_contract()
    {
        var request = Create(dataJson:
            "{\"commandId\":\"cmd-1\",\"reason\":\"watchdog_critical\",\"expiresAt\":\"2026-07-10T21:59:59.0000000+00:00\"}");

        var result = Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal("command_expiry_invalid_or_stale", result.Code);
    }

    private RemoteRepairValidationResult Validate(RemoteRepairRequest request) =>
        RemoteRepairContract.Validate(
            request,
            "agent-1",
            "fingerprint-1",
            Keys,
            Now);

    private RemoteRepairRequest Create(
        string command = "repair_agent",
        string? dataJson = null,
        DateTimeOffset? timestamp = null)
    {
        var commandAt = timestamp ?? Now;
        dataJson ??=
            $"{{\"commandId\":\"cmd-1\",\"reason\":\"watchdog_critical\",\"expiresAt\":\"{commandAt.AddMinutes(4):O}\"}}";
        var dataHash = RemoteCommandTrust.ComputeSha256Hex(dataJson);
        var canonical = RemoteCommandTrust.BuildCommandCanonical(
            command,
            "agent-1",
            "fingerprint-1",
            commandAt.ToString("O"),
            "nonce-1",
            dataHash);
        return new RemoteRepairRequest(
            RemoteRepairContract.SchemaVersion,
            command,
            "agent-1",
            "fingerprint-1",
            commandAt.ToString("O"),
            "nonce-1",
            KeyId,
            Convert.ToBase64String(_key.SignData(
                Encoding.UTF8.GetBytes(canonical),
                HashAlgorithmName.SHA256)),
            dataJson,
            dataHash,
            "cmd-1",
            "watchdog_critical",
            commandAt.ToString("O"));
    }

    public void Dispose() => _key.Dispose();
}
