namespace SuavoAgent.Attestation;

/// <summary>
/// Outcome of an attestation check.
/// </summary>
public sealed record AttestationResult(
    AttestationStatus Status,
    string? Reason,
    IReadOnlyList<FileMismatch>? Mismatches,
    string? ManifestVersion,
    long DurationMs)
{
    public static AttestationResult Verified(string manifestVersion, long durationMs) =>
        new(AttestationStatus.Verified, null, null, manifestVersion, durationMs);

    public static AttestationResult Mismatch(string manifestVersion, IReadOnlyList<FileMismatch> mismatches, long durationMs) =>
        new(AttestationStatus.Mismatch, $"{mismatches.Count} file(s) mismatched",
            mismatches, manifestVersion, durationMs);

    public static AttestationResult NetworkFailure(string reason, long durationMs) =>
        new(AttestationStatus.NetworkFailure, reason, null, null, durationMs);

    public static AttestationResult SignatureInvalid(string reason, long durationMs) =>
        new(AttestationStatus.SignatureInvalid, reason, null, null, durationMs);

    public static AttestationResult ConfigurationError(string reason) =>
        new(AttestationStatus.ConfigurationError, reason, null, null, 0);
}

public enum AttestationStatus
{
    /// <summary>All files in the manifest match on-disk hashes.</summary>
    Verified,

    /// <summary>
    /// One or more file hashes don't match. Halt all mutation verbs.
    /// This is a CRITICAL severity event — invariant.violated fires.
    /// </summary>
    Mismatch,

    /// <summary>
    /// Manifest signature failed verification. As severe as Mismatch —
    /// attacker may have produced fake manifest.
    /// </summary>
    SignatureInvalid,

    /// <summary>
    /// Could not fetch the manifest. Does NOT halt the agent — network
    /// partitions are expected. Re-check next interval. If >24h since last
    /// successful verify, escalate per phase-a-architecture.md §A6.
    /// </summary>
    NetworkFailure,

    /// <summary>Local misconfiguration — missing install dir, bad paths.</summary>
    ConfigurationError
}

public sealed record FileMismatch(
    string FileName,
    string ExpectedSha256,
    string? ActualSha256,
    string Reason);
