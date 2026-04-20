using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Canary;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class SchemaAdaptationPackagerTests
{
    [Fact]
    public void Pack_SignsWithProvidedKey_VerifyRoundTrip()
    {
        using var ecdsa = ECDsa.Create();
        var publicKeyDer = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        var packager = new SchemaAdaptationPackager(ecdsa, keyId: "adapt-v1");

        var deltas = new[]
        {
            new SchemaDelta("Prescription", "Rx", "PriceNew",
                "decimal(18,4)", "decimal(19,4)", true, false, "retyped"),
        };
        var rewrites = new[]
        {
            new QueryRewrite("old-hash",
                "SELECT TOP 1 RxNumber FROM Prescription.Rx WHERE Id = @p0",
                "new-hash"),
        };

        var adaptation = packager.Pack(
            adaptationId: "adapt-001",
            pmsType: "PioneerRx",
            fromSchemaHash: "from-hash",
            toSchemaHash: "to-hash",
            deltas: deltas,
            rewrites: rewrites,
            originPharmacyId: "hmac-origin",
            notBefore: DateTimeOffset.UtcNow,
            expiresAt: DateTimeOffset.UtcNow.AddDays(30));

        Assert.Equal("adapt-v1", adaptation.KeyId);
        Assert.False(string.IsNullOrEmpty(adaptation.Signature));

        // Verify the signature reproduces the canonical bytes.
        var bytes = SchemaAdaptationCanonicalV1.Build(adaptation);
        Assert.True(ecdsa.VerifyData(bytes, Convert.FromBase64String(adaptation.Signature),
            HashAlgorithmName.SHA256));
    }

    [Fact]
    public void Pack_RejectsTokenizerFailedRewrite()
    {
        using var ecdsa = ECDsa.Create();
        var packager = new SchemaAdaptationPackager(ecdsa, keyId: "adapt-v1");

        var bad = new[]
        {
            // This SQL contains a semicolon followed by another statement — SqlTokenizer rejects.
            new QueryRewrite("old", "SELECT 1; DROP TABLE x", "new"),
        };

        Assert.Throws<InvalidOperationException>(() => packager.Pack(
            adaptationId: "adapt-bad", pmsType: "PioneerRx",
            fromSchemaHash: "h1", toSchemaHash: "h2",
            deltas: Array.Empty<SchemaDelta>(),
            rewrites: bad,
            originPharmacyId: "o",
            notBefore: DateTimeOffset.UtcNow, expiresAt: DateTimeOffset.UtcNow.AddDays(1)));
    }
}

public class SchemaAdaptationApplierTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly ECDsa _cloudKey = ECDsa.Create();
    private readonly string _pubKeyDer;

    public SchemaAdaptationApplierTests()
    {
        _db = new AgentStateDb(":memory:");
        _pubKeyDer = Convert.ToBase64String(_cloudKey.ExportSubjectPublicKeyInfo());
    }

    public void Dispose()
    {
        _db.Dispose();
        _cloudKey.Dispose();
    }

    private SchemaAdaptation SignedSample(
        string id = "adapt-001",
        string fromHash = "from-hash",
        string toHash = "to-hash",
        DateTimeOffset? expiresAt = null)
    {
        var actualExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(30);
        // notBefore must be before expiresAt (Packager invariant). For the
        // "expired" test case, set notBefore an hour before the expiry.
        var notBefore = actualExpiresAt.AddHours(-1);

        var packager = new SchemaAdaptationPackager(_cloudKey, "adapt-v1");
        return packager.Pack(
            adaptationId: id, pmsType: "PioneerRx",
            fromSchemaHash: fromHash, toSchemaHash: toHash,
            deltas: new[]
            {
                new SchemaDelta("Prescription", "Rx", "PriceNew",
                    "decimal(18,4)", "decimal(19,4)", true, false, "retyped"),
            },
            rewrites: new[]
            {
                new QueryRewrite("old-shape",
                    "SELECT TOP 1 RxNumber FROM Prescription.Rx WHERE Id = @p0",
                    "new-shape"),
            },
            originPharmacyId: "hmac-origin",
            notBefore: notBefore,
            expiresAt: actualExpiresAt);
    }

    // ──────────────────────── happy path ────────────────────────

    [Fact]
    public void Apply_ValidSignature_FingerprintMatch_Applied()
    {
        var a = SignedSample();
        var applier = BuildApplier(localFromHash: "from-hash");
        var outcome = applier.ApplyIfEligible(a);
        Assert.Equal(SchemaAdaptationOutcome.Applied, outcome.Status);
        var row = _db.GetAppliedSchemaAdaptation(a.AdaptationId);
        Assert.NotNull(row);
    }

    [Fact]
    public void Apply_Idempotent_SecondApplyIsNoOp()
    {
        var a = SignedSample();
        var applier = BuildApplier(localFromHash: "from-hash");
        var first = applier.ApplyIfEligible(a);
        var second = applier.ApplyIfEligible(a);
        Assert.Equal(SchemaAdaptationOutcome.Applied, first.Status);
        Assert.Equal(SchemaAdaptationOutcome.AlreadyApplied, second.Status);
    }

    // ──────────────────────── fail-closed paths ────────────────────────

    [Fact]
    public void Apply_BadSignature_Rejected()
    {
        var a = SignedSample();
        // Tamper with the signed payload — signature no longer matches canonical bytes.
        var tampered = a with { ToSchemaHash = "tampered" };
        var applier = BuildApplier(localFromHash: "from-hash");
        var outcome = applier.ApplyIfEligible(tampered);
        Assert.Equal(SchemaAdaptationOutcome.SignatureInvalid, outcome.Status);
        Assert.Null(_db.GetAppliedSchemaAdaptation(tampered.AdaptationId));
    }

    [Fact]
    public void Apply_FingerprintMismatch_Skipped()
    {
        var a = SignedSample(fromHash: "cloud-from-hash");
        var applier = BuildApplier(localFromHash: "completely-different-hash");
        var outcome = applier.ApplyIfEligible(a);
        Assert.Equal(SchemaAdaptationOutcome.FingerprintMismatch, outcome.Status);
        Assert.Null(_db.GetAppliedSchemaAdaptation(a.AdaptationId));
    }

    [Fact]
    public void Apply_Expired_Skipped()
    {
        var a = SignedSample(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var applier = BuildApplier(localFromHash: "from-hash");
        var outcome = applier.ApplyIfEligible(a);
        Assert.Equal(SchemaAdaptationOutcome.Expired, outcome.Status);
    }

    [Fact]
    public void Apply_Revoked_SkippedAndAudited()
    {
        var a = SignedSample();
        _db.InsertSchemaAdaptationRevocation(a.AdaptationId, DateTimeOffset.UtcNow.ToString("o"), "admin-recall");
        var applier = BuildApplier(localFromHash: "from-hash");
        var outcome = applier.ApplyIfEligible(a);
        Assert.Equal(SchemaAdaptationOutcome.Revoked, outcome.Status);
        Assert.Null(_db.GetAppliedSchemaAdaptation(a.AdaptationId));
    }

    [Fact]
    public void Apply_RewriteFailsTokenizer_Rejected()
    {
        // Sign an adaptation with a malformed SQL first — since Packager refuses
        // this, we simulate by constructing the adaptation manually and signing
        // the canonical bytes through a helper.
        var badRewrite = new QueryRewrite("old", "SELECT 1; DROP TABLE y", "new");
        var raw = new SchemaAdaptation(
            AdaptationId: "adapt-bad",
            CanonicalVersion: SchemaAdaptationCanonicalV1.Version,
            PmsType: "PioneerRx",
            FromSchemaHash: "from-hash", ToSchemaHash: "to-hash",
            Deltas: Array.Empty<SchemaDelta>(),
            Rewrites: new[] { badRewrite },
            OriginPharmacyId: "o",
            NotBefore: DateTimeOffset.UtcNow.ToString("o"),
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(1).ToString("o"),
            KeyId: "adapt-v1",
            Signature: "");
        var bytes = SchemaAdaptationCanonicalV1.Build(raw);
        var sig = Convert.ToBase64String(_cloudKey.SignData(bytes, HashAlgorithmName.SHA256));
        var signed = raw with { Signature = sig };

        var applier = BuildApplier(localFromHash: "from-hash");
        var outcome = applier.ApplyIfEligible(signed);
        Assert.Equal(SchemaAdaptationOutcome.TokenizerRejected, outcome.Status);
        Assert.Null(_db.GetAppliedSchemaAdaptation(signed.AdaptationId));
    }

    [Fact]
    public void Revocation_AppliedAdaptation_RollsBack()
    {
        var a = SignedSample();
        var applier = BuildApplier(localFromHash: "from-hash");
        applier.ApplyIfEligible(a);
        Assert.NotNull(_db.GetAppliedSchemaAdaptation(a.AdaptationId));

        var revoked = applier.Revoke(a.AdaptationId, "cloud-recall");
        Assert.True(revoked);
        var after = _db.GetAppliedSchemaAdaptation(a.AdaptationId);
        Assert.NotNull(after);
        Assert.NotNull(after!.RolledBackAt);
        Assert.Equal("cloud-recall", after.RollbackReason);
    }

    private SchemaAdaptationApplier BuildApplier(string localFromHash) =>
        new(_db, _pubKeyDer,
            localFromSchemaHashProvider: () => localFromHash,
            NullLogger<SchemaAdaptationApplier>.Instance);
}
