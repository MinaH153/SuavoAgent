using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Pricing;

/// <summary>
/// PHI-free workstation proposal for one exact pricing observation contract.
/// The authenticated heartbeat transports this record to the control plane.
/// </summary>
public sealed record PricingApprovalProposal(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("proposalDigest")] string ProposalDigest,
    [property: JsonPropertyName("pharmacyId")] string PharmacyId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("machineFingerprint")] string MachineFingerprint,
    [property: JsonPropertyName("modality")] string Modality,
    [property: JsonPropertyName("schemaDigest")] string SchemaDigest,
    [property: JsonPropertyName("statusPolicyDigest")] string StatusPolicyDigest,
    [property: JsonPropertyName("costBasis")] string CostBasis,
    [property: JsonPropertyName("policyDigest")] string PolicyDigest,
    [property: JsonPropertyName("snapshotContract")] string SnapshotContract,
    [property: JsonPropertyName("freshnessSeconds")] long FreshnessSeconds,
    [property: JsonPropertyName("observedAtUtc")] DateTimeOffset ObservedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Signed control-plane receipt proving that one exact proposal was durably
/// accepted. The agent retains and stops retrying only this exact digest.
/// </summary>
public sealed record PricingApprovalProposalReceipt(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("proposalDigest")] string ProposalDigest,
    [property: JsonPropertyName("pharmacyId")] string PharmacyId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("machineFingerprint")] string MachineFingerprint,
    [property: JsonPropertyName("receivedAtUtc")] DateTimeOffset ReceivedAtUtc,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>
/// PIC decision signed by the control-plane command key after authenticated,
/// tenant-scoped approval. It is bound to one proposal, workstation and policy.
/// </summary>
public sealed record PricingApprovalGrant(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("approvalId")] string ApprovalId,
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("proposalDigest")] string ProposalDigest,
    [property: JsonPropertyName("pharmacyId")] string PharmacyId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("machineFingerprint")] string MachineFingerprint,
    [property: JsonPropertyName("approverId")] string ApproverId,
    [property: JsonPropertyName("approvedByRole")] string ApprovedByRole,
    [property: JsonPropertyName("modality")] string Modality,
    [property: JsonPropertyName("schemaDigest")] string SchemaDigest,
    [property: JsonPropertyName("statusPolicyDigest")] string StatusPolicyDigest,
    [property: JsonPropertyName("costBasis")] string CostBasis,
    [property: JsonPropertyName("policyDigest")] string PolicyDigest,
    [property: JsonPropertyName("snapshotContract")] string SnapshotContract,
    [property: JsonPropertyName("freshnessSeconds")] long FreshnessSeconds,
    [property: JsonPropertyName("issuedAtUtc")] DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>Append-only, control-plane-signed revocation for one installed grant.</summary>
public sealed record PricingApprovalRevocation(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("revocationId")] string RevocationId,
    [property: JsonPropertyName("approvalId")] string ApprovalId,
    [property: JsonPropertyName("proposalId")] string ProposalId,
    [property: JsonPropertyName("proposalDigest")] string ProposalDigest,
    [property: JsonPropertyName("pharmacyId")] string PharmacyId,
    [property: JsonPropertyName("agentId")] string AgentId,
    [property: JsonPropertyName("machineFingerprint")] string MachineFingerprint,
    [property: JsonPropertyName("policyDigest")] string PolicyDigest,
    [property: JsonPropertyName("reasonCode")] string ReasonCode,
    [property: JsonPropertyName("revokedAtUtc")] DateTimeOffset RevokedAtUtc,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("signature")] string Signature);

/// <summary>
/// Cross-platform canonicalization and verification contract shared by the
/// Windows agent and control plane. Canonicals are UTF-8 and ECDSA P-256
/// signatures use IEEE-P1363 fixed-field concatenation.
/// </summary>
public static class PricingApprovalContract
{
    public const int SchemaVersion = 1;
    public const string InstallCommandName = "install_pricing_cost_basis_approval";
    public const string RevokeCommandName = "revoke_pricing_cost_basis_approval";
    public const string CostPerUnitBasis = "cost_per_unit";
    public const string PharmacistInChargeRole = "pharmacist_in_charge";
    public const string SnapshotContractV1 = "source_policy_snapshot_v1";
    public const string ProposalCanonicalPrefix = "suavo.pricing-approval-proposal.v1";
    public const string ProposalReceiptCanonicalPrefix =
        "suavo.pricing-approval-proposal-receipt.v1";
    public const string GrantCanonicalPrefix = "suavo.pricing-approval-grant.v1";
    public const string RevocationCanonicalPrefix = "suavo.pricing-approval-revocation.v1";
    public static readonly TimeSpan ProposalLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan MaximumGrantLifetime = TimeSpan.FromDays(366);
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> RevocationReasons = new(
        new[]
        {
            "pic_revoked",
            "policy_superseded",
            "workstation_replaced",
            "pharmacy_offboarded",
            "security_response",
        },
        StringComparer.Ordinal);

    public static string ProposalCanonical(PricingApprovalProposal proposal) =>
        JoinCanonical(
            ProposalCanonicalPrefix,
            proposal.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            proposal.ProposalId,
            proposal.PharmacyId,
            proposal.AgentId,
            proposal.MachineFingerprint,
            proposal.Modality,
            proposal.SchemaDigest,
            proposal.StatusPolicyDigest,
            proposal.CostBasis,
            proposal.PolicyDigest,
            proposal.SnapshotContract,
            proposal.FreshnessSeconds.ToString(CultureInfo.InvariantCulture),
            Utc(proposal.ObservedAtUtc),
            Utc(proposal.ExpiresAtUtc));

    public static string ComputeProposalDigest(PricingApprovalProposal proposal) =>
        Sha256Hex(ProposalCanonical(proposal));

    public static string ProposalReceiptCanonical(
        PricingApprovalProposalReceipt receipt) => JoinCanonical(
            ProposalReceiptCanonicalPrefix,
            receipt.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            receipt.ProposalId,
            receipt.ProposalDigest,
            receipt.PharmacyId,
            receipt.AgentId,
            receipt.MachineFingerprint,
            Utc(receipt.ReceivedAtUtc));

    public static string ComputeProposalReceiptDigest(
        PricingApprovalProposalReceipt receipt) => Sha256Hex(AppendSignatureMaterial(
            ProposalReceiptCanonical(receipt),
            receipt.KeyId,
            receipt.Signature));

    public static string GrantCanonical(PricingApprovalGrant grant) =>
        JoinCanonical(
            GrantCanonicalPrefix,
            grant.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            grant.ApprovalId,
            grant.ProposalId,
            grant.ProposalDigest,
            grant.PharmacyId,
            grant.AgentId,
            grant.MachineFingerprint,
            grant.ApproverId,
            grant.ApprovedByRole,
            grant.Modality,
            grant.SchemaDigest,
            grant.StatusPolicyDigest,
            grant.CostBasis,
            grant.PolicyDigest,
            grant.SnapshotContract,
            grant.FreshnessSeconds.ToString(CultureInfo.InvariantCulture),
            Utc(grant.IssuedAtUtc),
            Utc(grant.ExpiresAtUtc));

    public static string ComputeGrantDigest(PricingApprovalGrant grant) =>
        Sha256Hex(AppendSignatureMaterial(
            GrantCanonical(grant),
            grant.KeyId,
            grant.Signature));

    public static string RevocationCanonical(PricingApprovalRevocation revocation) =>
        JoinCanonical(
            RevocationCanonicalPrefix,
            revocation.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            revocation.RevocationId,
            revocation.ApprovalId,
            revocation.ProposalId,
            revocation.ProposalDigest,
            revocation.PharmacyId,
            revocation.AgentId,
            revocation.MachineFingerprint,
            revocation.PolicyDigest,
            revocation.ReasonCode,
            Utc(revocation.RevokedAtUtc));

    public static string ComputeRevocationDigest(PricingApprovalRevocation revocation) =>
        Sha256Hex(AppendSignatureMaterial(
            RevocationCanonical(revocation),
            revocation.KeyId,
            revocation.Signature));

    public static string ComputeObservationPolicyDigest(
        string modality,
        string schemaDigest,
        string statusPolicyDigest,
        string costBasis,
        string snapshotContract,
        long freshnessSeconds) => LengthPrefixedDigest(
            "pricing_observation_policy_v1",
            modality,
            schemaDigest,
            statusPolicyDigest,
            costBasis,
            snapshotContract,
            freshnessSeconds.ToString(CultureInfo.InvariantCulture));

    public static bool IsValidProposal(
        PricingApprovalProposal? proposal,
        DateTimeOffset now,
        out string code)
    {
        code = "pricing_approval_proposal_invalid";
        if (proposal is null || proposal.SchemaVersion != SchemaVersion ||
            !CanonicalUuid(proposal.ProposalId) ||
            !SafeToken(proposal.PharmacyId, 160) ||
            !SafeToken(proposal.AgentId, 160) ||
            !SafeToken(proposal.MachineFingerprint, 256) ||
            proposal.Modality is not ("sql" or "uia" or "vision") ||
            !LowerHex64(proposal.SchemaDigest) ||
            !LowerHex64(proposal.StatusPolicyDigest) ||
            proposal.CostBasis != CostPerUnitBasis ||
            !LowerHex64(proposal.PolicyDigest) ||
            proposal.SnapshotContract != SnapshotContractV1 ||
            proposal.FreshnessSeconds is < 1 or > 86_400 ||
            proposal.ObservedAtUtc > now + MaximumFutureSkew ||
            proposal.ExpiresAtUtc <= proposal.ObservedAtUtc ||
            proposal.ExpiresAtUtc - proposal.ObservedAtUtc > ProposalLifetime ||
            !FixedHexEquals(
                proposal.PolicyDigest,
                ComputeObservationPolicyDigest(
                    proposal.Modality,
                    proposal.SchemaDigest,
                    proposal.StatusPolicyDigest,
                    proposal.CostBasis,
                    proposal.SnapshotContract,
                    proposal.FreshnessSeconds)) ||
            !FixedHexEquals(
                proposal.ProposalDigest,
                ComputeProposalDigest(proposal)))
            return false;
        code = proposal.ExpiresAtUtc <= now
            ? "pricing_approval_proposal_expired"
            : "valid";
        return proposal.ExpiresAtUtc > now;
    }

    public static bool IsValidGrant(
        PricingApprovalGrant? grant,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        out string code)
    {
        code = "pricing_approval_grant_invalid";
        if (grant is null || grant.SchemaVersion != SchemaVersion ||
            !CanonicalUuid(grant.ApprovalId) ||
            !CanonicalUuid(grant.ProposalId) ||
            !LowerHex64(grant.ProposalDigest) ||
            !SafeToken(grant.PharmacyId, 160) ||
            !SafeToken(grant.AgentId, 160) ||
            !SafeToken(grant.MachineFingerprint, 256) ||
            !SafeToken(grant.ApproverId, 160) ||
            grant.ApprovedByRole != PharmacistInChargeRole ||
            grant.Modality is not ("sql" or "uia" or "vision") ||
            !LowerHex64(grant.SchemaDigest) ||
            !LowerHex64(grant.StatusPolicyDigest) ||
            grant.CostBasis != CostPerUnitBasis ||
            !LowerHex64(grant.PolicyDigest) ||
            grant.SnapshotContract != SnapshotContractV1 ||
            grant.FreshnessSeconds is < 1 or > 86_400 ||
            !SafeToken(grant.KeyId, 64) ||
            grant.IssuedAtUtc > now + MaximumFutureSkew ||
            grant.ExpiresAtUtc <= grant.IssuedAtUtc ||
            grant.ExpiresAtUtc - grant.IssuedAtUtc > MaximumGrantLifetime ||
            !FixedHexEquals(
                grant.PolicyDigest,
                ComputeObservationPolicyDigest(
                    grant.Modality,
                    grant.SchemaDigest,
                    grant.StatusPolicyDigest,
                    grant.CostBasis,
                    grant.SnapshotContract,
                    grant.FreshnessSeconds)))
            return false;
        if (!VerifySignature(
                GrantCanonical(grant),
                grant.KeyId,
                grant.Signature,
                trustedPublicKeys))
        {
            code = "pricing_approval_grant_signature_invalid";
            return false;
        }
        if (grant.ExpiresAtUtc <= now)
        {
            code = "pricing_approval_grant_expired";
            return false;
        }
        code = "valid";
        return true;
    }

    public static bool IsValidProposalReceipt(
        PricingApprovalProposalReceipt? receipt,
        PricingApprovalProposal proposal,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        out string code)
    {
        code = "pricing_approval_proposal_receipt_invalid";
        if (receipt is null || receipt.SchemaVersion != SchemaVersion ||
            !CanonicalUuid(receipt.ProposalId) ||
            !LowerHex64(receipt.ProposalDigest) ||
            !SafeToken(receipt.PharmacyId, 160) ||
            !SafeToken(receipt.AgentId, 160) ||
            !SafeToken(receipt.MachineFingerprint, 256) ||
            !SafeToken(receipt.KeyId, 64) ||
            receipt.ProposalId != proposal.ProposalId ||
            !FixedHexEquals(receipt.ProposalDigest, proposal.ProposalDigest) ||
            receipt.PharmacyId != proposal.PharmacyId ||
            receipt.AgentId != proposal.AgentId ||
            receipt.MachineFingerprint != proposal.MachineFingerprint ||
            receipt.ReceivedAtUtc < proposal.ObservedAtUtc - MaximumFutureSkew ||
            receipt.ReceivedAtUtc > now + MaximumFutureSkew)
            return false;
        if (!VerifySignature(
                ProposalReceiptCanonical(receipt),
                receipt.KeyId,
                receipt.Signature,
                trustedPublicKeys))
        {
            code = "pricing_approval_proposal_receipt_signature_invalid";
            return false;
        }
        code = "valid";
        return true;
    }

    public static bool IsValidRevocation(
        PricingApprovalRevocation? revocation,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        out string code)
    {
        code = "pricing_approval_revocation_invalid";
        if (revocation is null || revocation.SchemaVersion != SchemaVersion ||
            !CanonicalUuid(revocation.RevocationId) ||
            !CanonicalUuid(revocation.ApprovalId) ||
            !CanonicalUuid(revocation.ProposalId) ||
            !LowerHex64(revocation.ProposalDigest) ||
            !SafeToken(revocation.PharmacyId, 160) ||
            !SafeToken(revocation.AgentId, 160) ||
            !SafeToken(revocation.MachineFingerprint, 256) ||
            !LowerHex64(revocation.PolicyDigest) ||
            !RevocationReasons.Contains(revocation.ReasonCode) ||
            !SafeToken(revocation.KeyId, 64) ||
            revocation.RevokedAtUtc > now + MaximumFutureSkew)
            return false;
        if (!VerifySignature(
                RevocationCanonical(revocation),
                revocation.KeyId,
                revocation.Signature,
                trustedPublicKeys))
        {
            code = "pricing_approval_revocation_signature_invalid";
            return false;
        }
        code = "valid";
        return true;
    }

    public static bool GrantMatchesProposal(
        PricingApprovalGrant grant,
        PricingApprovalProposal proposal) =>
        grant.ProposalId == proposal.ProposalId &&
        FixedHexEquals(grant.ProposalDigest, proposal.ProposalDigest) &&
        grant.PharmacyId == proposal.PharmacyId &&
        grant.AgentId == proposal.AgentId &&
        grant.MachineFingerprint == proposal.MachineFingerprint &&
        grant.Modality == proposal.Modality &&
        FixedHexEquals(grant.SchemaDigest, proposal.SchemaDigest) &&
        FixedHexEquals(grant.StatusPolicyDigest, proposal.StatusPolicyDigest) &&
        grant.CostBasis == proposal.CostBasis &&
        FixedHexEquals(grant.PolicyDigest, proposal.PolicyDigest) &&
        grant.SnapshotContract == proposal.SnapshotContract &&
        grant.FreshnessSeconds == proposal.FreshnessSeconds &&
        grant.IssuedAtUtc >= proposal.ObservedAtUtc - MaximumFutureSkew &&
        grant.IssuedAtUtc <= proposal.ExpiresAtUtc;

    public static bool RevocationMatchesGrant(
        PricingApprovalRevocation revocation,
        PricingApprovalGrant grant) =>
        revocation.ApprovalId == grant.ApprovalId &&
        revocation.ProposalId == grant.ProposalId &&
        FixedHexEquals(revocation.ProposalDigest, grant.ProposalDigest) &&
        revocation.PharmacyId == grant.PharmacyId &&
        revocation.AgentId == grant.AgentId &&
        revocation.MachineFingerprint == grant.MachineFingerprint &&
        FixedHexEquals(revocation.PolicyDigest, grant.PolicyDigest) &&
        revocation.RevokedAtUtc >= grant.IssuedAtUtc - MaximumFutureSkew;

    private static bool VerifySignature(
        string canonical,
        string keyId,
        string signature,
        IReadOnlyDictionary<string, string> trustedPublicKeys)
    {
        if (!trustedPublicKeys.TryGetValue(keyId, out var publicKey) ||
            string.IsNullOrWhiteSpace(publicKey) ||
            string.IsNullOrWhiteSpace(signature))
            return false;
        byte[]? keyBytes = null;
        byte[]? signatureBytes = null;
        try
        {
            keyBytes = Convert.FromBase64String(publicKey);
            signatureBytes = Convert.FromBase64String(signature);
            if (signatureBytes.Length != 64) return false;
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(keyBytes, out var consumed);
            return consumed == keyBytes.Length && verifier.KeySize == 256 &&
                   verifier.VerifyData(
                       Encoding.UTF8.GetBytes(canonical),
                       signatureBytes,
                       HashAlgorithmName.SHA256,
                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is
            FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (keyBytes is not null) CryptographicOperations.ZeroMemory(keyBytes);
            if (signatureBytes is not null) CryptographicOperations.ZeroMemory(signatureBytes);
        }
    }

    private static string JoinCanonical(params string[] values)
    {
        if (values.Any(value => value.Contains('|', StringComparison.Ordinal)))
            throw new ArgumentException("Pricing approval canonical fields cannot contain separators.");
        return string.Join('|', values);
    }

    private static string AppendSignatureMaterial(
        string canonical,
        string keyId,
        string signature)
    {
        if (keyId.Contains('|', StringComparison.Ordinal) ||
            signature.Contains('|', StringComparison.Ordinal))
            throw new ArgumentException(
                "Pricing approval signature fields cannot contain separators.");
        return string.Concat(canonical, "|", keyId, "|", signature);
    }

    private static string LengthPrefixedDigest(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthPrefix = stackalloc byte[sizeof(int)];
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, bytes.Length);
            hash.AppendData(lengthPrefix);
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static bool CanonicalUuid(string value) =>
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);

    private static bool SafeToken(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength &&
        value.All(character => char.IsAsciiLetterOrDigit(character) ||
                               character is '-' or '_' or '.' or ':');

    private static bool LowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedHexEquals(string? left, string? right)
    {
        if (!LowerHex64(left) || !LowerHex64(right)) return false;
        var leftBytes = Convert.FromHexString(left!);
        var rightBytes = Convert.FromHexString(right!);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }
}
