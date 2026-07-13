using System.Security.Cryptography;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record DurableUpdateClaim(
    ValidatedUpdateClaim Validated,
    string ClaimDirectory,
    string RequestPath,
    string PayloadDirectory,
    bool WasAlreadyClaimed);

internal sealed record DurableUpdateClaimResult(
    bool Succeeded,
    string Code,
    DurableUpdateClaim? Claim = null)
{
    public static DurableUpdateClaimResult Success(DurableUpdateClaim claim) =>
        new(true, "claimed", claim);
    public static DurableUpdateClaimResult Fail(string code) => new(false, code);
}

/// <summary>
/// Copies a LocalService-writable activation request and payload into an
/// Administrator/SYSTEM-only claim directory, re-verifies the copied bytes,
/// and reserves the authoritative replay identity before the source request is
/// eligible for deletion.
/// </summary>
internal sealed class NativeUpdateClaimStore
{
    private readonly string _maintenanceRoot;
    private readonly NativeUpdateClaimValidator _validator;
    private readonly AuthoritativeUpdateReplayLedger _ledger;
    private readonly Action<string> _lockdown;
    private readonly string _sourceUpdateRoot;

    public NativeUpdateClaimStore(
        string maintenanceRoot,
        NativeUpdateClaimValidator validator,
        AuthoritativeUpdateReplayLedger ledger,
        Action<string>? lockdown = null,
        string? sourceUpdateRoot = null)
    {
        _maintenanceRoot = Path.GetFullPath(maintenanceRoot);
        _validator = validator;
        _ledger = ledger;
        _lockdown = lockdown ?? ServiceInstaller.LockdownMaintenanceDirectoryAcl;
        _sourceUpdateRoot = Path.GetFullPath(
            sourceUpdateRoot ?? UpdateActivationContract.DefaultUpdateRoot());
    }

    public DurableUpdateClaimResult Claim(
        string sourceRequestPath,
        string sourcePayloadDirectory,
        InstalledUpdateIdentity identity,
        DateTimeOffset now)
    {
        if (!IsExactUntrustedPaths(sourceRequestPath, sourcePayloadDirectory))
            return DurableUpdateClaimResult.Fail("source_path_not_fixed");
        var source = _validator.Validate(
            sourceRequestPath,
            sourcePayloadDirectory,
            identity,
            now);
        if (!source.IsValid)
            return DurableUpdateClaimResult.Fail(source.Code);
        var validated = source.Claim!;
        var claimDirectory = Path.Combine(
            _maintenanceRoot,
            UpdateActivationContract.CoordinatorDirectoryName,
            validated.Request.StagingId);
        var trustedRequestPath = Path.Combine(
            claimDirectory,
            UpdateActivationContract.ActivationRequestFileName);
        var trustedPayload = Path.Combine(claimDirectory, "payload");

        try
        {
            Directory.CreateDirectory(_maintenanceRoot);
            _lockdown(_maintenanceRoot);
            if (Directory.Exists(claimDirectory))
            {
                var existing = _validator.Validate(
                    trustedRequestPath,
                    trustedPayload,
                    identity,
                    now);
                if (!existing.IsValid ||
                    !string.Equals(
                        existing.Claim!.ReplayId,
                        validated.ReplayId,
                        StringComparison.Ordinal))
                    return DurableUpdateClaimResult.Fail("existing_claim_conflict");
                if (!_ledger.TryReserve(
                        validated.ReplayId,
                        validated.Request.StagingId,
                        validated.Manifest.Version,
                        now,
                        out _))
                    return DurableUpdateClaimResult.Fail("authoritative_replay_rejected");
                return DurableUpdateClaimResult.Success(new DurableUpdateClaim(
                    existing.Claim,
                    claimDirectory,
                    trustedRequestPath,
                    trustedPayload,
                    WasAlreadyClaimed: true));
            }

            var tempClaim = claimDirectory + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(tempClaim);
                _lockdown(tempClaim);
                var tempPayload = Path.Combine(tempClaim, "payload");
                Directory.CreateDirectory(tempPayload);
                foreach (var file in validated.Manifest.Files)
                    CopyAndVerify(
                        Path.Combine(sourcePayloadDirectory, file.FileName),
                        Path.Combine(tempPayload, file.FileName),
                        file.Sha256);
                File.WriteAllBytes(
                    Path.Combine(tempClaim, UpdateActivationContract.ActivationRequestFileName),
                    validated.RequestBytes);

                var copied = _validator.Validate(
                    Path.Combine(tempClaim, UpdateActivationContract.ActivationRequestFileName),
                    tempPayload,
                    identity,
                    now);
                if (!copied.IsValid ||
                    !string.Equals(copied.Claim!.ReplayId, validated.ReplayId, StringComparison.Ordinal))
                    return DurableUpdateClaimResult.Fail("trusted_copy_validation_failed:" + copied.Code);

                Directory.Move(tempClaim, claimDirectory);
            }
            finally
            {
                try { if (Directory.Exists(tempClaim)) Directory.Delete(tempClaim, true); } catch { }
            }

            if (!_ledger.TryReserve(
                    validated.ReplayId,
                    validated.Request.StagingId,
                    validated.Manifest.Version,
                    now,
                    out _))
                return DurableUpdateClaimResult.Fail("authoritative_replay_rejected");

            var finalValidation = _validator.Validate(
                trustedRequestPath,
                trustedPayload,
                identity,
                now);
            if (!finalValidation.IsValid)
                return DurableUpdateClaimResult.Fail("durable_claim_validation_failed:" + finalValidation.Code);
            return DurableUpdateClaimResult.Success(new DurableUpdateClaim(
                finalValidation.Claim!,
                claimDirectory,
                trustedRequestPath,
                trustedPayload,
                WasAlreadyClaimed: false));
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            CryptographicException or
            ArgumentException)
        {
            return DurableUpdateClaimResult.Fail("claim_persist_failed:" + ex.GetType().Name);
        }
    }

    private static void CopyAndVerify(string source, string destination, string expectedSha256)
    {
        BoundedFile.CopyAndHashVerify(
            source,
            destination,
            200L * 1024 * 1024,
            expectedSha256);
    }

    private bool IsExactUntrustedPaths(string requestPath, string payloadDirectory)
    {
        try
        {
            if (!Path.IsPathFullyQualified(requestPath) ||
                !Path.IsPathFullyQualified(payloadDirectory) ||
                !string.Equals(
                    Path.GetFullPath(requestPath),
                    Path.GetFullPath(Path.Combine(
                        _sourceUpdateRoot,
                        UpdateActivationContract.ActivationRequestFileName)),
                    StringComparison.OrdinalIgnoreCase))
                return false;
            var json = BoundedFile.ReadUtf8(
                requestPath,
                UpdateActivationContract.MaxRequestBytes);
            if (!UpdateActivationContract.TryDeserialize(json, out var request, out _))
                return false;
            var expectedPayload = UpdateActivationContract.GetIncomingStagingDirectory(
                _sourceUpdateRoot,
                request!.StagingId);
            return string.Equals(
                Path.GetFullPath(payloadDirectory),
                Path.GetFullPath(expectedPayload),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
