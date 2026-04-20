using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.Canary;

/// <summary>
/// Packages a resolved Schema Canary drift into a signed
/// <see cref="SchemaAdaptation"/> ready for cloud upload. Every
/// <see cref="QueryRewrite.NewParameterizedSql"/> is validated through
/// <see cref="SqlTokenizer"/> before signing — a single bad rewrite rejects
/// the entire package (fail closed).
///
/// The packager does NOT send anything over the network; it only produces the
/// signed contract. Transport layer lives in a separate cloud worker.
/// </summary>
public sealed class SchemaAdaptationPackager
{
    private readonly ECDsa _key;
    private readonly string _keyId;

    public SchemaAdaptationPackager(ECDsa key, string keyId)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        if (string.IsNullOrWhiteSpace(keyId))
            throw new ArgumentException("keyId required", nameof(keyId));
        _keyId = keyId;
    }

    public SchemaAdaptation Pack(
        string adaptationId,
        string pmsType,
        string fromSchemaHash,
        string toSchemaHash,
        IReadOnlyList<SchemaDelta> deltas,
        IReadOnlyList<QueryRewrite> rewrites,
        string originPharmacyId,
        DateTimeOffset notBefore,
        DateTimeOffset expiresAt)
    {
        if (expiresAt <= notBefore)
            throw new ArgumentException(
                "expiresAt must be after notBefore", nameof(expiresAt));

        foreach (var r in rewrites)
        {
            if (SqlTokenizer.TryNormalize(r.NewParameterizedSql) is null)
                throw new InvalidOperationException(
                    $"SchemaAdaptationPackager: QueryRewrite {r.OldShapeHash}→{r.NewShapeHash} " +
                    "failed SqlTokenizer validation — refusing to sign");
        }

        var unsigned = new SchemaAdaptation(
            AdaptationId: adaptationId,
            CanonicalVersion: SchemaAdaptationCanonicalV1.Version,
            PmsType: pmsType,
            FromSchemaHash: fromSchemaHash,
            ToSchemaHash: toSchemaHash,
            Deltas: deltas,
            Rewrites: rewrites,
            OriginPharmacyId: originPharmacyId,
            NotBefore: notBefore.ToString("o"),
            ExpiresAt: expiresAt.ToString("o"),
            KeyId: _keyId,
            Signature: "");

        var bytes = SchemaAdaptationCanonicalV1.Build(unsigned);
        var sig = Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256));

        return unsigned with { Signature = sig };
    }

    /// <summary>
    /// Packages a signed revocation. Uses <see cref="AdaptationRevocationCanonicalV1"/>
    /// so receivers can verify independently of the envelope transport.
    /// </summary>
    public AdaptationRevocation PackRevocation(
        string revocationId,
        string targetAdaptationId,
        string reason,
        DateTimeOffset revokedAt)
    {
        var unsigned = new AdaptationRevocation(
            RevocationId: revocationId,
            TargetAdaptationId: targetAdaptationId,
            Reason: reason,
            RevokedAt: revokedAt.ToString("o"),
            KeyId: _keyId,
            Signature: "");

        var bytes = AdaptationRevocationCanonicalV1.Build(unsigned);
        var sig = Convert.ToBase64String(_key.SignData(bytes, HashAlgorithmName.SHA256));

        return unsigned with { Signature = sig };
    }
}
