using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Learning;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Learning;

public class SchemaAdaptationTests
{
    private static readonly SchemaDelta SampleDelta = new(
        "Prescription", "Rx", "PriceNew", "decimal(18,4)", "decimal(19,4)",
        OldNullable: true, NewNullable: false, ChangeKind: "retyped");

    private static readonly QueryRewrite SampleRewrite = new(
        OldShapeHash: "old-hash-1",
        NewParameterizedSql: "SELECT TOP 1 RxNumber FROM Prescription.Rx WHERE RxNumber = @p0",
        NewShapeHash: "new-hash-1");

    private static SchemaAdaptation BuildSample(string version = SchemaAdaptationCanonicalV1.Version)
    {
        return new SchemaAdaptation(
            AdaptationId: "adapt-001",
            CanonicalVersion: version,
            PmsType: "PioneerRx",
            FromSchemaHash: "from-hash",
            ToSchemaHash: "to-hash",
            Deltas: new[] { SampleDelta },
            Rewrites: new[] { SampleRewrite },
            OriginPharmacyId: "hmac-origin-xyz",
            NotBefore: "2026-04-19T00:00:00Z",
            ExpiresAt: "2026-05-19T00:00:00Z",
            KeyId: "adapt-v1",
            Signature: "not-yet-signed");
    }

    [Fact]
    public void Canonical_Deterministic()
    {
        var a1 = BuildSample();
        var a2 = BuildSample();
        var bytes1 = SchemaAdaptationCanonicalV1.Build(a1);
        var bytes2 = SchemaAdaptationCanonicalV1.Build(a2);
        Assert.Equal(bytes1, bytes2);
    }

    [Fact]
    public void Canonical_VersionMismatch_Throws()
    {
        var bad = BuildSample("SomeOtherVersion");
        Assert.Throws<ArgumentException>(() => SchemaAdaptationCanonicalV1.Build(bad));
    }

    [Fact]
    public void Canonical_ChangeInField_ChangesBytes()
    {
        var a = BuildSample();
        var bytes = SchemaAdaptationCanonicalV1.Build(a);
        var mutated = a with { FromSchemaHash = "mutated-hash" };
        var bytesMutated = SchemaAdaptationCanonicalV1.Build(mutated);
        Assert.NotEqual(bytes, bytesMutated);
    }

    [Fact]
    public void Canonical_ChangeInDelta_ChangesBytes()
    {
        var a = BuildSample();
        var otherDelta = SampleDelta with { NewDataType = "decimal(20,4)" };
        var mutated = a with { Deltas = new[] { otherDelta } };
        Assert.NotEqual(
            SchemaAdaptationCanonicalV1.Build(a),
            SchemaAdaptationCanonicalV1.Build(mutated));
    }

    [Fact]
    public void Canonical_ChangeInRewriteSql_ChangesBytes()
    {
        var a = BuildSample();
        var otherRewrite = SampleRewrite with
        {
            NewParameterizedSql = "SELECT RxNumber FROM Rx WHERE Id = @p0"
        };
        var mutated = a with { Rewrites = new[] { otherRewrite } };
        Assert.NotEqual(
            SchemaAdaptationCanonicalV1.Build(a),
            SchemaAdaptationCanonicalV1.Build(mutated));
    }

    [Fact]
    public void Canonical_IsNotSameAsSignedCommandBytes()
    {
        // Codex Area 3: canonical form must not silently match
        // SignedCommand's "command|agentId|fingerprint|timestamp|nonce|dataHash" layout.
        var a = BuildSample();
        var bytes = SchemaAdaptationCanonicalV1.Build(a);
        var text = Encoding.UTF8.GetString(bytes);
        // Must lead with the canonical version identifier.
        Assert.StartsWith("SchemaAdaptationCanonicalV1\n", text);
        // Must NOT contain a '|'-separated single-line command-like layout.
        var firstLine = text.Split('\n')[0];
        Assert.DoesNotContain("|", firstLine);
    }

    [Fact]
    public void RoundTripSignVerify_RealEcdsa()
    {
        using var ecdsa = ECDsa.Create();
        var a = BuildSample();
        var bytes = SchemaAdaptationCanonicalV1.Build(a);
        var sig = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        Assert.True(ecdsa.VerifyData(bytes, sig, HashAlgorithmName.SHA256));

        // Flip the adaptation after signing — verification must fail.
        var tampered = a with { ToSchemaHash = "tampered" };
        var tamperedBytes = SchemaAdaptationCanonicalV1.Build(tampered);
        Assert.False(ecdsa.VerifyData(tamperedBytes, sig, HashAlgorithmName.SHA256));
    }
}

public class AdaptationRevocationTests
{
    [Fact]
    public void Canonical_Deterministic()
    {
        var r = new AdaptationRevocation("rev-1", "adapt-1", "bad-rewrite", "2026-04-19T10:00:00Z", "adapt-v1", "sig");
        var b1 = AdaptationRevocationCanonicalV1.Build(r);
        var b2 = AdaptationRevocationCanonicalV1.Build(r);
        Assert.Equal(b1, b2);
    }

    [Fact]
    public void Canonical_DoesNotIncludeReason()
    {
        // Reason can be free text (capped elsewhere); keeping it out of the
        // canonical signing bytes avoids PHI leakage paths through the
        // revocation channel.
        var r = new AdaptationRevocation("rev-1", "adapt-1", "maybe-sensitive", "2026-04-19T10:00:00Z", "adapt-v1", "sig");
        var text = Encoding.UTF8.GetString(AdaptationRevocationCanonicalV1.Build(r));
        Assert.DoesNotContain("maybe-sensitive", text);
    }

    [Fact]
    public void Canonical_VersionPrefix()
    {
        var r = new AdaptationRevocation("rev-1", "adapt-1", "x", "ts", "adapt-v1", "sig");
        var text = Encoding.UTF8.GetString(AdaptationRevocationCanonicalV1.Build(r));
        Assert.StartsWith("AdaptationRevocationCanonicalV1\n", text);
    }

    [Fact]
    public void RoundTripSignVerify()
    {
        using var ecdsa = ECDsa.Create();
        var r = new AdaptationRevocation("rev-1", "adapt-1", "reason", "2026-04-19T10:00:00Z", "adapt-v1", "");
        var bytes = AdaptationRevocationCanonicalV1.Build(r);
        var sig = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        Assert.True(ecdsa.VerifyData(bytes, sig, HashAlgorithmName.SHA256));
    }
}
