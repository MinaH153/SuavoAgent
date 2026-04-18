namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Manages local model files on disk. Today this just verifies an
/// operator-placed file; Week 2c adds background download from a signed
/// GitHub release manifest.
/// </summary>
public interface IModelManager
{
    /// <summary>
    /// Returns true if a valid model is present at the configured path and
    /// passes SHA-256 verification (when a hash is configured).
    /// Fail-closed: any IO error, missing file, or hash mismatch returns false.
    /// </summary>
    Task<ModelVerificationResult> VerifyAsync(CancellationToken ct);
}

public sealed record ModelVerificationResult(
    bool IsValid,
    string? Path,
    string? Sha256Actual,
    string? Reason);
