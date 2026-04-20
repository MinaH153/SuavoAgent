using System;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Canary;

public enum SchemaAdaptationOutcome
{
    Applied,
    AlreadyApplied,
    SignatureInvalid,
    FingerprintMismatch,
    Expired,
    Revoked,
    TokenizerRejected,
    NotYetActive,
}

public sealed record SchemaAdaptationApplyResult(SchemaAdaptationOutcome Status, string? Detail);

/// <summary>
/// Receiver-side application of fleet schema adaptations. Fails closed on every
/// safety check — invalid signature, mismatched local PMS fingerprint,
/// expired, revoked, or tokenizer-rejected rewrite — and records the attempt
/// on the first audit-able surface (<see cref="AgentStateDb.InsertSchemaAdaptationRevocation"/>
/// or <see cref="AgentStateDb.InsertAppliedSchemaAdaptation"/>).
///
/// Revocation handling: any <see cref="SchemaAdaptationOutcome.Revoked"/> or
/// explicit <see cref="Revoke"/> call triggers a rollback on a prior apply.
/// </summary>
public sealed class SchemaAdaptationApplier
{
    private readonly AgentStateDb _db;
    private readonly string _publicKeyDer;
    private readonly Func<string> _localFromSchemaHashProvider;
    private readonly ILogger<SchemaAdaptationApplier> _logger;

    public SchemaAdaptationApplier(
        AgentStateDb db, string publicKeyDer,
        Func<string> localFromSchemaHashProvider,
        ILogger<SchemaAdaptationApplier> logger)
    {
        _db = db;
        _publicKeyDer = publicKeyDer;
        _localFromSchemaHashProvider = localFromSchemaHashProvider;
        _logger = logger;
    }

    public SchemaAdaptationApplyResult ApplyIfEligible(SchemaAdaptation adaptation)
    {
        if (_db.GetAppliedSchemaAdaptation(adaptation.AdaptationId) is { } existing
            && existing.RolledBackAt is null)
        {
            return new(SchemaAdaptationOutcome.AlreadyApplied, "already-applied");
        }

        // Revocation gate runs BEFORE signature — a denylisted adaptation
        // shouldn't burn compute on signature verification.
        if (_db.IsSchemaAdaptationRevoked(adaptation.AdaptationId))
        {
            _logger.LogInformation(
                "SchemaAdaptationApplier: {Id} is revoked — skipping",
                adaptation.AdaptationId);
            return new(SchemaAdaptationOutcome.Revoked, "denylisted");
        }

        // Signature verification against canonical bytes.
        if (!VerifySignature(adaptation))
        {
            _logger.LogWarning(
                "SchemaAdaptationApplier: {Id} has invalid signature — dropping",
                adaptation.AdaptationId);
            return new(SchemaAdaptationOutcome.SignatureInvalid, "signature-invalid");
        }

        // NotBefore gate — fail closed on parse failure too. Activation window
        // semantics match X.509/JWT nbf: an adaptation may be signed + published
        // ahead of its effective date for staged/maintenance-window rollouts.
        if (!DateTimeOffset.TryParse(adaptation.NotBefore, out var notBefore)
            || notBefore > DateTimeOffset.UtcNow)
        {
            _logger.LogInformation(
                "SchemaAdaptationApplier: {Id} not yet active (NotBefore={At})",
                adaptation.AdaptationId, adaptation.NotBefore);
            return new(SchemaAdaptationOutcome.NotYetActive, "not-yet-active");
        }

        // Expiry gate.
        if (!DateTimeOffset.TryParse(adaptation.ExpiresAt, out var expiresAt)
            || expiresAt <= DateTimeOffset.UtcNow)
        {
            _logger.LogInformation(
                "SchemaAdaptationApplier: {Id} expired at {At}",
                adaptation.AdaptationId, adaptation.ExpiresAt);
            return new(SchemaAdaptationOutcome.Expired, "expired");
        }

        // Local fingerprint must match FromSchemaHash.
        var localFrom = _localFromSchemaHashProvider() ?? "";
        if (!string.Equals(localFrom, adaptation.FromSchemaHash, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "SchemaAdaptationApplier: {Id} FromSchemaHash {From} does not match local {Local}",
                adaptation.AdaptationId, adaptation.FromSchemaHash, localFrom);
            return new(SchemaAdaptationOutcome.FingerprintMismatch, "fingerprint-mismatch");
        }

        // Every rewrite must re-validate through SqlTokenizer on the receiver
        // side — defense in depth against a signer that was compromised or
        // a deterministic serialization drift.
        foreach (var r in adaptation.Rewrites)
        {
            if (SqlTokenizer.TryNormalize(r.NewParameterizedSql) is null)
            {
                _logger.LogWarning(
                    "SchemaAdaptationApplier: {Id} rewrite {Old}→{New} rejected by tokenizer",
                    adaptation.AdaptationId, r.OldShapeHash, r.NewShapeHash);
                return new(SchemaAdaptationOutcome.TokenizerRejected, "tokenizer-rejected");
            }
        }

        var rewritesJson = JsonSerializer.Serialize(adaptation.Rewrites);
        _db.InsertAppliedSchemaAdaptation(
            adaptation.AdaptationId,
            adaptation.FromSchemaHash,
            adaptation.ToSchemaHash,
            rewritesJson,
            DateTimeOffset.UtcNow.ToString("o"));

        _logger.LogInformation(
            "SchemaAdaptationApplier: applied {Id} ({From} → {To}, {Count} rewrite(s))",
            adaptation.AdaptationId, adaptation.FromSchemaHash, adaptation.ToSchemaHash,
            adaptation.Rewrites.Count);
        return new(SchemaAdaptationOutcome.Applied, null);
    }

    /// <summary>
    /// In-process revocation path — trusts the caller. Only call this from
    /// local operator tooling or other in-process code where provenance is
    /// already established. Cloud-delivered revocations MUST go through
    /// <see cref="RevokeSigned"/> so the per-record signature is checked.
    /// </summary>
    public bool Revoke(string adaptationId, string reason)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        _db.InsertSchemaAdaptationRevocation(adaptationId, now, reason);

        var existing = _db.GetAppliedSchemaAdaptation(adaptationId);
        if (existing is null || existing.RolledBackAt is not null) return false;

        _db.RollbackAppliedSchemaAdaptation(adaptationId, now, reason);
        _logger.LogInformation(
            "SchemaAdaptationApplier: rolled back {Id} (reason: {Reason})", adaptationId, reason);
        return true;
    }

    /// <summary>
    /// Cloud-delivered revocation path. Verifies the per-record ECDSA signature
    /// against <see cref="AdaptationRevocationCanonicalV1"/> before mutating
    /// state. Mirrors the defense-in-depth check that
    /// <see cref="ApplyIfEligible"/> runs on adaptations even though the
    /// envelope transport already verified the pull response.
    /// </summary>
    public bool RevokeSigned(AdaptationRevocation rev)
    {
        if (rev is null) throw new ArgumentNullException(nameof(rev));
        if (!VerifyRevocationSignature(rev))
        {
            _logger.LogWarning(
                "SchemaAdaptationApplier: revocation {Id} (target {Target}) has invalid signature — dropping",
                rev.RevocationId, rev.TargetAdaptationId);
            return false;
        }
        return Revoke(rev.TargetAdaptationId, rev.Reason ?? "cloud-revocation");
    }

    private bool VerifyRevocationSignature(AdaptationRevocation rev)
    {
        try
        {
            byte[] bytes;
            try { bytes = AdaptationRevocationCanonicalV1.Build(rev); }
            catch { return false; }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(_publicKeyDer), out _);

            if (string.IsNullOrEmpty(rev.Signature)) return false;
            var sig = Convert.FromBase64String(rev.Signature);
            return ecdsa.VerifyData(bytes, sig, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    private bool VerifySignature(SchemaAdaptation adaptation)
    {
        try
        {
            byte[] bytes;
            try
            {
                bytes = SchemaAdaptationCanonicalV1.Build(adaptation);
            }
            catch { return false; }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(
                Convert.FromBase64String(_publicKeyDer), out _);

            var sig = Convert.FromBase64String(adaptation.Signature);
            return ecdsa.VerifyData(bytes, sig, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }
}
