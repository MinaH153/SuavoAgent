using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Contracts.Learning;

/// <summary>
/// One column-level schema delta detected by Schema Canary.
/// Field ordering inside <see cref="CanonicalRepr"/> is part of the signing
/// contract (<see cref="SchemaAdaptationCanonicalV1.Build"/>) — changing it
/// is a canonical-version bump.
/// </summary>
public sealed record SchemaDelta(
    string SchemaName,
    string TableName,
    string ColumnName,
    string OldDataType,
    string NewDataType,
    bool OldNullable,
    bool NewNullable,
    string ChangeKind)
{
    public string CanonicalRepr =>
        $"{SchemaName}|{TableName}|{ColumnName}|{OldDataType}|{NewDataType}|" +
        $"{(OldNullable ? 1 : 0)}|{(NewNullable ? 1 : 0)}|{ChangeKind}";
}

/// <summary>
/// A specific SQL-shape rewrite produced by the adaptation. The before/after
/// mapping is what receivers install into the per-shape override store.
/// <see cref="NewParameterizedSql"/> MUST pass <c>SqlTokenizer.TryNormalize</c>
/// before the record is signed or applied.
/// </summary>
public sealed record QueryRewrite(string OldShapeHash, string NewParameterizedSql, string NewShapeHash)
{
    public string CanonicalRepr
    {
        get
        {
            var sqlHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(NewParameterizedSql ?? string.Empty)))
                .ToLowerInvariant();
            return $"{OldShapeHash}|{NewShapeHash}|{sqlHash}";
        }
    }
}

/// <summary>
/// Fleet-wide propagation payload: a signed, PHI-free description of how a
/// local pharmacy resolved a Schema Canary drift. Receivers verify signature,
/// fingerprint-match, and re-tokenize every query before applying.
/// </summary>
public sealed record SchemaAdaptation(
    string AdaptationId,
    string CanonicalVersion,
    string PmsType,
    string FromSchemaHash,
    string ToSchemaHash,
    IReadOnlyList<SchemaDelta> Deltas,
    IReadOnlyList<QueryRewrite> Rewrites,
    string OriginPharmacyId,
    string NotBefore,
    string ExpiresAt,
    string KeyId,
    string Signature);

/// <summary>
/// Signed revocation for a previously-distributed adaptation. Receivers
/// persist the target id in a denylist and roll back if already applied.
/// </summary>
public sealed record AdaptationRevocation(
    string RevocationId,
    string TargetAdaptationId,
    string Reason,
    string RevokedAt,
    string KeyId,
    string Signature);

/// <summary>
/// Canonical byte layout for <see cref="SchemaAdaptation"/> signing + verify
/// (Codex Area 3 fix). Do NOT reuse <c>SignedCommand</c> canonical bytes —
/// that format is scoped to agent→cloud command envelopes and has different
/// replay semantics.
///
/// Layout (UTF-8, line-joined by <c>\n</c>, no trailing newline):
/// <list type="number">
///   <item>CanonicalVersion</item>
///   <item>AdaptationId</item>
///   <item>PmsType</item>
///   <item>FromSchemaHash</item>
///   <item>ToSchemaHash</item>
///   <item>DeltasHash     = SHA-256 over delta.CanonicalRepr joined with '\n'</item>
///   <item>RewritesHash   = SHA-256 over rewrite.CanonicalRepr joined with '\n'</item>
///   <item>OriginPharmacyId</item>
///   <item>NotBefore</item>
///   <item>ExpiresAt</item>
///   <item>KeyId</item>
/// </list>
/// </summary>
public static class SchemaAdaptationCanonicalV1
{
    public const string Version = "SchemaAdaptationCanonicalV1";

    public static byte[] Build(SchemaAdaptation a)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        if (!string.Equals(a.CanonicalVersion, Version, StringComparison.Ordinal))
            throw new ArgumentException(
                $"SchemaAdaptation.CanonicalVersion must be '{Version}'", nameof(a));

        var deltasText = string.Join('\n', a.Deltas.Select(d => d.CanonicalRepr));
        var rewritesText = string.Join('\n', a.Rewrites.Select(r => r.CanonicalRepr));

        var deltasHash = Sha256Hex(deltasText);
        var rewritesHash = Sha256Hex(rewritesText);

        var text = string.Join('\n',
            a.CanonicalVersion,
            a.AdaptationId,
            a.PmsType,
            a.FromSchemaHash,
            a.ToSchemaHash,
            deltasHash,
            rewritesHash,
            a.OriginPharmacyId,
            a.NotBefore,
            a.ExpiresAt,
            a.KeyId);

        return Encoding.UTF8.GetBytes(text);
    }

    private static string Sha256Hex(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
}

/// <summary>
/// Canonical byte layout for <see cref="AdaptationRevocation"/> signing.
/// Small surface on purpose — receivers only need to confirm the target
/// id is authentic.
/// </summary>
public static class AdaptationRevocationCanonicalV1
{
    public const string Version = "AdaptationRevocationCanonicalV1";

    public static byte[] Build(AdaptationRevocation r)
    {
        if (r is null) throw new ArgumentNullException(nameof(r));
        var text = string.Join('\n',
            Version,
            r.RevocationId,
            r.TargetAdaptationId,
            r.RevokedAt,
            r.KeyId);
        return Encoding.UTF8.GetBytes(text);
    }
}
