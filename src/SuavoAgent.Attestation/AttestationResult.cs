namespace SuavoAgent.Attestation;

/// <summary>
/// Outcome of an attestation check.
/// </summary>
public sealed record AttestationResult(
    AttestationStatus Status,
    string? Reason,
    IReadOnlyList<FileMismatch>? Mismatches,
    string? ManifestVersion,
    int FileCount,
    long DurationMs)
{
    public static AttestationResult Verified(string manifestVersion, int fileCount, long durationMs) =>
        new(AttestationStatus.Verified, null, null, manifestVersion, fileCount, durationMs);

    public static AttestationResult Mismatch(string manifestVersion, int fileCount, IReadOnlyList<FileMismatch> mismatches, long durationMs) =>
        new(AttestationStatus.Mismatch, $"{mismatches.Count} file(s) mismatched",
            mismatches, manifestVersion, fileCount, durationMs);

    public static AttestationResult NetworkFailure(string reason, long durationMs) =>
        new(AttestationStatus.NetworkFailure, reason, null, null, 0, durationMs);

    public static AttestationResult SignatureInvalid(string reason, long durationMs) =>
        new(AttestationStatus.SignatureInvalid, reason, null, null, 0, durationMs);

    public static AttestationResult ConfigurationError(string reason) =>
        new(AttestationStatus.ConfigurationError, reason, null, null, 0, 0);
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
