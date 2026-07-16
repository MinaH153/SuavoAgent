using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class ObservationActivationRequestSignerTests : IDisposable
{
    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string PharmacyId = "22222222-2222-4222-8222-222222222222";
    private const string Fingerprint = "machine.fp-1";
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"suavo-observation-request-{Guid.NewGuid():N}.db");

    [Fact]
    public void Create_BindsExactCanonical_UsesP1363_AndPersistsMonotonicCounter()
    {
        using var keys = new InMemoryDeviceAttestationKeyProvider();
        string publicKey;
        string keyId;
        using (var pending = keys.OpenOrCreate(Fingerprint))
        {
            publicKey = pending.Enrollment.PublicKeySpki;
            keyId = pending.Enrollment.KeyId;
            keys.CommitPending(Fingerprint, keyId);
        }

        var identity = Identity(keyId);
        var clock = new FixedTimeProvider(
            DateTimeOffset.Parse("2026-07-15T18:01:02.345Z"));
        SignedObservationActivationLeaseRequest first;
        SignedObservationActivationLeaseRequest second;
        using (var db = new AgentStateDb(_dbPath))
        {
            var signer = new ObservationActivationRequestSigner(identity, db, keys, clock);
            first = signer.Create(knownGeneration: 7);
            second = signer.Create(knownGeneration: 7);
        }

        Assert.Equal(1, first.Counter);
        Assert.Equal(2, second.Counter);
        Assert.Equal("2026-07-15T18:01:02.345Z", first.RequestedAtUtc);
        Assert.True(IsCanonicalV4Uuid(first.RequestNonce));
        Assert.NotEqual(first.RequestNonce, second.RequestNonce);
        Assert.Equal(86, first.Signature.Length);
        Assert.DoesNotContain('=', first.Signature);

        var fields = new ObservationActivationLeaseRequestFields(
            first.SchemaVersion,
            first.AgentId,
            first.PharmacyId,
            first.WorkstationId,
            first.MachineFingerprint,
            first.DeviceKeyId,
            first.ReleaseCohort,
            first.PolicyDigest,
            first.KnownGeneration,
            first.Counter,
            first.RequestedAtUtc,
            first.RequestNonce);
        var canonical = ObservationActivationRequestSigner.BuildCanonical(fields);
        Assert.Equal(
            "suavo.observation-lease-request.v1\n" +
            "schemaVersion=1\n" +
            $"agentId={AgentId}\n" +
            $"pharmacyId={PharmacyId}\n" +
            $"workstationId={AgentId}\n" +
            $"machineFingerprint={Fingerprint}\n" +
            $"deviceKeyId={keyId}\n" +
            "releaseCohort=pharmacy-field-rc\n" +
            $"policyDigest={ObservationActivationIdentityStore.PolicyDigest}\n" +
            "knownGeneration=7\n" +
            "counter=1\n" +
            "requestedAtUtc=2026-07-15T18:01:02.345Z\n" +
            $"requestNonce={first.RequestNonce}",
            canonical);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant(),
            first.CanonicalDigest);

        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        Assert.True(verifier.VerifyData(
            Encoding.UTF8.GetBytes(canonical),
            DecodeBase64Url(first.Signature),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        using var reopened = new AgentStateDb(_dbPath);
        var afterRestart = new ObservationActivationRequestSigner(
            identity,
            reopened,
            keys,
            clock).Create(knownGeneration: 8);
        Assert.Equal(3, afterRestart.Counter);
        Assert.Equal(8, afterRestart.KnownGeneration);
    }

    [Fact]
    public void SerializedBody_HasOnlyTheFrozenLowerCamelPropertiesInOrder()
    {
        var request = new SignedObservationActivationLeaseRequest(
            1, AgentId, PharmacyId, AgentId, Fingerprint, new string('a', 64),
            "pharmacy-field-rc", ObservationActivationIdentityStore.PolicyDigest,
            4, 9, "2026-07-15T18:01:02.345Z",
            "33333333-3333-4333-8333-333333333333", new string('b', 64),
            new string('A', 86));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.Equal(
            new[]
            {
                "schemaVersion", "agentId", "pharmacyId", "workstationId",
                "machineFingerprint", "deviceKeyId", "releaseCohort", "policyDigest",
                "knownGeneration", "counter", "requestedAtUtc", "requestNonce",
                "canonicalDigest", "signature",
            },
            document.RootElement.EnumerateObject().Select(property => property.Name));
        OutboundPhiGuard.AssertAllowed(
            SuavoCloudClient.ObservationActivationLeasePath,
            document.RootElement.GetRawText(),
            new AgentOptions { StrictOutboundTokenAllowlist = true });
    }

    [Fact]
    public void SigningFailure_BurnsCounter_AndNextRequestAdvances()
    {
        using var keys = new InMemoryDeviceAttestationKeyProvider();
        string keyId;
        using (var pending = keys.OpenOrCreate(Fingerprint))
        {
            keyId = pending.Enrollment.KeyId;
            keys.CommitPending(Fingerprint, keyId);
        }

        using var db = new AgentStateDb(_dbPath);
        var signer = new ObservationActivationRequestSigner(
            Identity(keyId),
            db,
            new FailFirstSignProvider(keys),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-07-15T18:01:02.345Z")));

        Assert.Throws<CryptographicException>(() => signer.Create(knownGeneration: 0));
        Assert.Equal(2, signer.Create(knownGeneration: 0).Counter);
    }

    [Fact]
    public void LeaseParser_RequiresExactRequestBinding()
    {
        var requestDigest = new string('a', 64);
        var json = "{\"command\":\"observation_activation_lease_v1\"," +
            $"\"agentId\":\"{AgentId}\",\"machineFingerprint\":\"{Fingerprint}\"," +
            "\"timestamp\":\"2026-07-15T18:01:02.345Z\"," +
            "\"nonce\":\"33333333-3333-4333-8333-333333333333\"," +
            "\"keyId\":\"key-1\",\"signature\":\"signature\"," +
            $"\"dataHash\":\"{new string('b', 64)}\",\"data\":{{" +
            "\"schemaVersion\":1," +
            "\"leaseId\":\"44444444-4444-4444-8444-444444444444\"," +
            "\"requestId\":\"55555555-5555-4555-8555-555555555555\"," +
            $"\"requestDigest\":\"{requestDigest}\",\"pharmacyId\":\"{PharmacyId}\"," +
            $"\"workstationId\":\"{AgentId}\",\"deviceKeyId\":\"{new string('c', 64)}\"," +
            "\"releaseCohort\":\"pharmacy-field-rc\",\"generation\":8," +
            $"\"policyDigest\":\"{ObservationActivationIdentityStore.PolicyDigest}\"," +
            "\"issuedAtUtc\":\"2026-07-15T18:01:02.345Z\"," +
            "\"notBeforeUtc\":\"2026-07-15T18:01:02.345Z\"," +
            "\"expiresAtUtc\":\"2026-07-15T18:03:02.345Z\"," +
            "\"authorizationId\":\"66666666-6666-4666-8666-666666666666\"}}";
        using var document = JsonDocument.Parse(json);

        Assert.True(SuavoCloudClient.TryParseObservationActivationLease(
            document.RootElement,
            requestDigest,
            out var state));
        Assert.NotNull(state);
        Assert.False(SuavoCloudClient.TryParseObservationActivationLease(
            document.RootElement,
            new string('d', 64),
            out _));
    }

    private static ObservationActivationIdentity Identity(string keyId) => new(
        AgentId,
        AgentId,
        PharmacyId,
        Fingerprint,
        keyId,
        "pharmacy-field-rc",
        ObservationActivationIdentityStore.PolicyDigest);

    private static bool IsCanonicalV4Uuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal) &&
        value[14] == '4' && value[19] is '8' or '9' or 'a' or 'b';

    private static byte[] DecodeBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += new string('=', (4 - base64.Length % 4) % 4);
        return Convert.FromBase64String(base64);
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FailFirstSignProvider(IDeviceAttestationKeyProvider inner) :
        IDeviceAttestationKeyProvider
    {
        private int _failNext = 1;

        public IDeviceAttestationKey OpenExisting(string authoritativeFingerprint) =>
            new FailFirstSignKey(
                inner.OpenExisting(authoritativeFingerprint),
                () => Interlocked.Exchange(ref _failNext, 0) == 1);

        public IDeviceAttestationKey OpenOrCreate(string authoritativeFingerprint) =>
            inner.OpenOrCreate(authoritativeFingerprint);

        public IDeviceAttestationKey OpenExistingForMaintenance(string authoritativeFingerprint) =>
            inner.OpenExistingForMaintenance(authoritativeFingerprint);

        public DeviceMaintenanceSignature SignForMaintenance(
            string authoritativeFingerprint,
            string expectedActiveKeyId,
            ReadOnlyMemory<byte> canonicalBytes) =>
            inner.SignForMaintenance(authoritativeFingerprint, expectedActiveKeyId, canonicalBytes);

        public IDeviceAttestationKey OpenVersion(
            string authoritativeFingerprint,
            string expectedKeyName,
            string expectedKeyId) =>
            inner.OpenVersion(authoritativeFingerprint, expectedKeyName, expectedKeyId);

        public bool IsActiveVersion(
            string authoritativeFingerprint,
            string expectedKeyName,
            string expectedKeyId) =>
            inner.IsActiveVersion(authoritativeFingerprint, expectedKeyName, expectedKeyId);

        public bool IsPendingVersion(
            string authoritativeFingerprint,
            string expectedKeyName,
            string expectedKeyId) =>
            inner.IsPendingVersion(authoritativeFingerprint, expectedKeyName, expectedKeyId);

        public void CommitPending(string authoritativeFingerprint, string expectedKeyId) =>
            inner.CommitPending(authoritativeFingerprint, expectedKeyId);

        public void AbortPending(string authoritativeFingerprint, string expectedKeyId) =>
            inner.AbortPending(authoritativeFingerprint, expectedKeyId);

        public void DestroyForUninstall(
            string authoritativeFingerprint,
            string expectedActiveKeyId) =>
            inner.DestroyForUninstall(authoritativeFingerprint, expectedActiveKeyId);
    }

    private sealed class FailFirstSignKey(
        IDeviceAttestationKey inner,
        Func<bool> shouldFail) : IDeviceAttestationKey
    {
        public DeviceKeyEnrollment Enrollment => inner.Enrollment;
        public string LocalKeyName => inner.LocalKeyName;

        public byte[] Sign(ReadOnlySpan<byte> canonicalBytes)
        {
            if (shouldFail())
                throw new CryptographicException("simulated signing failure");
            return inner.Sign(canonicalBytes);
        }

        public void Dispose() => inner.Dispose();
    }
}
