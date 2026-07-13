using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class DeviceAuthorityCanonicalTests
{
    private const string Expected = """
        {"agentId":"22222222-2222-4222-8222-222222222222","approvedBy":"55555555-5555-4555-8555-555555555555","approvedModelDigest":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","approvedTemplateDigest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","commandId":"11111111-1111-4111-8111-111111111111","commandPayloadDigest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc","completedAt":"2026-07-10T20:00:00.0000000Z","counter":7,"fingerprint":"machine.fp-1","pharmacyId":"33333333-3333-4333-8333-333333333333","pomId":"44444444-4444-4444-8444-444444444444","resultCode":"pom_approval_activated","schemaVersion":1,"sessionId":"session-1"}
        """;

    [Fact]
    public void PomReceipt_MatchesCloudGoldenVector()
    {
        var receipt = new PomActivationDeviceReceipt(
            1,
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "33333333-3333-4333-8333-333333333333",
            "machine.fp-1",
            "44444444-4444-4444-8444-444444444444",
            "session-1",
            new string('a', 64),
            new string('b', 64),
            "55555555-5555-4555-8555-555555555555",
            "pom_approval_activated",
            7,
            "2026-07-10T20:00:00.0000000Z",
            new string('c', 64));

        var canonical = DeviceAuthorityCanonical.Serialize(receipt);
        Assert.Equal(Expected, canonical);
        var domainBytes = Encoding.UTF8.GetBytes(
            $"suavo.pom-activation.v1\n{canonical}");
        Assert.Equal(
            "a74eec11112053b748941658858326d7d1dff48ace98c66390ee6069b97bef47",
            Convert.ToHexString(SHA256.HashData(domainBytes)).ToLowerInvariant());
    }

    [Fact]
    public void SeedApplicationReceipt_MatchesCloudGoldenVector()
    {
        var receipt = new SeedApplicationDeviceReceipt(
            1,
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "33333333-3333-4333-8333-333333333333",
            new string('a', 64),
            new string('b', 64),
            1770000000,
            "model",
            new string('c', 64),
            "learn-session-1",
            "2026-07-11T08:00:00.0000000Z",
            5,
            2,
            7);
        const string expected = """
            {"agentId":"22222222-2222-4222-8222-222222222222","appliedAt":"2026-07-11T08:00:00.0000000Z","commandId":"11111111-1111-4111-8111-111111111111","correlationsApplied":5,"correlationsSkipped":2,"counter":7,"deviceKeyId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","pharmacyId":"33333333-3333-4333-8333-333333333333","phase":"model","schemaVersion":1,"seedDigest":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","seedVersion":1770000000,"sessionId":"learn-session-1","sourceManifestDigest":"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}
            """;

        var canonical = DeviceAuthorityCanonical.Serialize(receipt);

        Assert.Equal(expected, canonical);
        Assert.Equal(
            "938718bf873728d488de3939c52319004d37e10904cfeee11aa93df75eaa938d",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"suavo.seed-application.v1\n{canonical}"))).ToLowerInvariant());
    }

    [Fact]
    public void ProbationHealth_MatchesFrozenCanonicalAndEveryFieldIsSignatureBound()
    {
        var health = Health();
        var expected = "suavo.device-probation-health.v3\n" +
                       "device-code-1\n" +
                       "11111111-1111-4111-8111-111111111111\n" +
                       "22222222-2222-4222-8222-222222222222\n" +
                       "33333333-3333-4333-8333-333333333333\n" +
                       "machine.fp-1\n" +
                       "3.80.0\n" +
                       new string('a', 64) + "\n" +
                       "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n" +
                       "sqlServerCertificateSha256=none\n" +
                       "observedAtUtc=2026-07-13T08:00:00.0000000Z\n" +
                       "challengeCounter=1\n" +
                       "helperAttached=false\n" +
                       "ipcConnected=false\n" +
                       "actuationReady=false\n" +
                       "sqlConnected=true\n" +
                       "schemaCanaryGreen=true\n" +
                       "pmsCode=pms_schema_canary";
        Assert.Equal(expected, DeviceProbationHealthCanonical.Serialize(health));
        Assert.False(expected.EndsWith("\n", StringComparison.Ordinal));

        using var keys = new InMemoryDeviceAttestationKeyProvider();
        string publicKey;
        using (var pending = keys.OpenOrCreate(health.Fingerprint))
        {
            publicKey = pending.Enrollment.PublicKeySpki;
            keys.CommitPending(health.Fingerprint, pending.Enrollment.KeyId);
        }
        var options = new AgentOptions { MachineFingerprint = health.Fingerprint };
        using var signer = new DeviceAuthoritySigner(options, keys);
        health = health with { KeyId = signer.KeyId };
        var signed = signer.SignProbationHealth(health);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        var signature = DecodeBase64Url(signed.Signature);
        Assert.True(Verify(verifier, health, signature));
        Assert.Throws<InvalidOperationException>(() =>
            DeviceProbationHealthCanonical.Serialize(
                health with { ChallengeCounter = 2 }));

        var mutations = new[]
        {
            health with { DeviceCode = "device-code-2" },
            health with { ProvisioningId = "41111111-1111-4111-8111-111111111111" },
            health with { AgentId = "42222222-2222-4222-8222-222222222222" },
            health with { PharmacyId = "43333333-3333-4333-8333-333333333333" },
            health with { Fingerprint = "machine.fp-2" },
            health with { Version = "3.80.1" },
            health with { KeyId = new string('b', 64) },
            health with { Challenge = "BAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" },
            health with { SqlServerCertificateSha256 = new string('c', 64) },
            health with { ObservedAtUtc = "2026-07-13T08:00:01.0000000Z" },
            health with { HelperAttached = true },
            health with { IpcConnected = true },
            health with { ActuationReady = true },
            health with { SqlConnected = false },
            health with { SchemaCanaryGreen = false },
            health with { PmsCode = "pms_db_unreachable" },
        };
        Assert.All(mutations, mutation => Assert.False(Verify(verifier, mutation, signature)));
    }

    [Fact]
    public void ProvisioningV2CanonicalizesExplicitNullAndExactCertificateDigest()
    {
        var unbound = new DeviceProvisioningProofFields(
            "device-code-1",
            "11111111-1111-4111-8111-111111111111",
            "22222222-2222-4222-8222-222222222222",
            "33333333-3333-4333-8333-333333333333",
            "machine.fp-1",
            new string('a', 64),
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            SqlServerCertificateSha256: null);
        var digest = new string('c', 64);
        var expectedNull = "suavo.device-provisioning.v2\n" +
                           "device-code-1\n" +
                           "11111111-1111-4111-8111-111111111111\n" +
                           "22222222-2222-4222-8222-222222222222\n" +
                           "33333333-3333-4333-8333-333333333333\n" +
                           "machine.fp-1\n" +
                           new string('a', 64) + "\n" +
                           "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n" +
                           "sqlServerCertificateSha256=none";

        Assert.Equal(expectedNull, DeviceProvisioningProofCanonical.Serialize(unbound));
        Assert.Equal(
            expectedNull.Replace(
                "sqlServerCertificateSha256=none",
                $"sqlServerCertificateSha256={digest}",
                StringComparison.Ordinal),
            DeviceProvisioningProofCanonical.Serialize(unbound with
            {
                SqlServerCertificateSha256 = digest,
            }));
        Assert.NotEqual(
            DeviceProvisioningProofCanonical.Digest(unbound),
            DeviceProvisioningProofCanonical.Digest(unbound with
            {
                SqlServerCertificateSha256 = digest,
            }));
    }

    [Fact]
    public void ProbationHealthV3CanonicalizesExactCertificateDigest()
    {
        var digest = new string('c', 64);
        var serialized = DeviceProbationHealthCanonical.Serialize(Health() with
        {
            SqlServerCertificateSha256 = digest,
        });

        Assert.Contains(
            $"\nsqlServerCertificateSha256={digest}\n",
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sqlServerCertificateSha256=none",
            serialized,
            StringComparison.Ordinal);
    }

    private static DeviceProbationHealthFields Health() => new(
        "device-code-1",
        "11111111-1111-4111-8111-111111111111",
        "22222222-2222-4222-8222-222222222222",
        "33333333-3333-4333-8333-333333333333",
        "machine.fp-1",
        "3.80.0",
        new string('a', 64),
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
        false,
        false,
        false,
        true,
        true,
        "pms_schema_canary",
        SqlServerCertificateSha256: null,
        ObservedAtUtc: "2026-07-13T08:00:00.0000000Z",
        ChallengeCounter: 1);

    private static bool Verify(
        ECDsa key,
        DeviceProbationHealthFields health,
        byte[] signature) =>
        key.VerifyData(
            Encoding.UTF8.GetBytes(DeviceProbationHealthCanonical.Serialize(health)),
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + (4 - normalized.Length % 4) % 4,
            '=');
        return Convert.FromBase64String(normalized);
    }
}
