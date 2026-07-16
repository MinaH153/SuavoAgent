using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Cloud;

internal interface IObservationActivationRequestSigner
{
    SignedObservationActivationLeaseRequest Create(long knownGeneration);
}

internal sealed class ObservationActivationRequestSigner :
    IObservationActivationRequestSigner
{
    internal const string CanonicalDomain = "suavo.observation-lease-request.v1";

    private readonly ObservationActivationIdentity? _identity;
    private readonly AgentStateDb _stateDb;
    private readonly IDeviceAttestationKeyProvider? _deviceKeys;
    private readonly TimeProvider _clock;

    internal ObservationActivationRequestSigner(
        ObservationActivationIdentity? identity,
        AgentStateDb stateDb,
        IDeviceAttestationKeyProvider? deviceKeys = null,
        TimeProvider? clock = null)
    {
        _identity = identity;
        _stateDb = stateDb;
        _deviceKeys = deviceKeys;
        _clock = clock ?? TimeProvider.System;
    }

    public SignedObservationActivationLeaseRequest Create(long knownGeneration)
    {
        if (_identity is null)
            throw new InvalidOperationException(
                "Observation activation identity is unavailable.");
        if (knownGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(knownGeneration));

        var provider = _deviceKeys ?? DeviceAttestationKeyProvider.CreateProduction();
        using var key = provider.OpenExisting(_identity.MachineFingerprint);
        if (!string.Equals(
                key.Enrollment.KeyId,
                _identity.DeviceKeyId,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Active device key does not match the observation identity.");

        var counter = _stateDb.NextObservationActivationRequestCounter(
            _identity.AgentId,
            _identity.DeviceKeyId);
        var fields = new ObservationActivationLeaseRequestFields(
            1,
            _identity.AgentId,
            _identity.PharmacyId,
            _identity.WorkstationId,
            _identity.MachineFingerprint,
            _identity.DeviceKeyId,
            _identity.ReleaseCohort,
            _identity.PolicyDigest,
            knownGeneration,
            counter,
            _clock.GetUtcNow().UtcDateTime.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString("D"));
        var canonical = BuildCanonical(fields);
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            var signature = key.Sign(canonicalBytes);
            try
            {
                if (signature.Length != 64)
                    throw new CryptographicException(
                        "The TPM returned an invalid observation request signature.");
                return new(
                    fields.SchemaVersion,
                    fields.AgentId,
                    fields.PharmacyId,
                    fields.WorkstationId,
                    fields.MachineFingerprint,
                    fields.DeviceKeyId,
                    fields.ReleaseCohort,
                    fields.PolicyDigest,
                    fields.KnownGeneration,
                    fields.Counter,
                    fields.RequestedAtUtc,
                    fields.RequestNonce,
                    Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant(),
                    Base64Url(signature));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(signature);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
        }
    }

    internal static string BuildCanonical(ObservationActivationLeaseRequestFields fields) =>
        $"{CanonicalDomain}\n" +
        $"schemaVersion={fields.SchemaVersion}\n" +
        $"agentId={fields.AgentId}\n" +
        $"pharmacyId={fields.PharmacyId}\n" +
        $"workstationId={fields.WorkstationId}\n" +
        $"machineFingerprint={fields.MachineFingerprint}\n" +
        $"deviceKeyId={fields.DeviceKeyId}\n" +
        $"releaseCohort={fields.ReleaseCohort}\n" +
        $"policyDigest={fields.PolicyDigest}\n" +
        $"knownGeneration={fields.KnownGeneration}\n" +
        $"counter={fields.Counter}\n" +
        $"requestedAtUtc={fields.RequestedAtUtc}\n" +
        $"requestNonce={fields.RequestNonce}";

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed record ObservationActivationLeaseRequestFields(
    int SchemaVersion,
    string AgentId,
    string PharmacyId,
    string WorkstationId,
    string MachineFingerprint,
    string DeviceKeyId,
    string ReleaseCohort,
    string PolicyDigest,
    long KnownGeneration,
    long Counter,
    string RequestedAtUtc,
    string RequestNonce);

internal sealed record SignedObservationActivationLeaseRequest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("pharmacyId")] string PharmacyId,
    [property: JsonPropertyName("workstationId")] string WorkstationId,
    [property: JsonPropertyName("machineFingerprint")] string MachineFingerprint,
    [property: JsonPropertyName("deviceKeyId")] string DeviceKeyId,
    [property: JsonPropertyName("releaseCohort")] string ReleaseCohort,
    [property: JsonPropertyName("policyDigest")] string PolicyDigest,
    [property: JsonPropertyName("knownGeneration")] long KnownGeneration,
    [property: JsonPropertyName("counter")] long Counter,
    [property: JsonPropertyName("requestedAtUtc")] string RequestedAtUtc,
    [property: JsonPropertyName("requestNonce")] string RequestNonce,
    [property: JsonPropertyName("canonicalDigest")] string CanonicalDigest,
    [property: JsonPropertyName("signature")] string Signature);
