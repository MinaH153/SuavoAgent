using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Verifies an operator-placed model file on disk. Does NOT download anything
/// today — that's Week 2c (signed manifest + GitHub release). For now the
/// operator is expected to drop the .gguf file at ReasoningOptions.ModelPath,
/// optionally with a known-good SHA-256 for integrity verification.
/// </summary>
public sealed class LocalFileModelManager : IModelManager
{
    private readonly ReasoningOptions _options;
    private readonly ILogger<LocalFileModelManager> _logger;

    public LocalFileModelManager(
        IOptions<AgentOptions> agentOptions,
        ILogger<LocalFileModelManager> logger)
    {
        _options = agentOptions.Value.Reasoning;
        _logger = logger;
    }

    public async Task<ModelVerificationResult> VerifyAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelPath))
            return new ModelVerificationResult(false, null, null, "ModelPath not configured");

        if (!File.Exists(_options.ModelPath))
            return new ModelVerificationResult(false, _options.ModelPath, null,
                $"Model file missing at {_options.ModelPath}");

        // No expected hash configured — presence is enough. Log warning so
        // operators are aware they opted out of integrity verification.
        if (string.IsNullOrWhiteSpace(_options.ModelSha256))
        {
            _logger.LogWarning(
                "ReasoningOptions.ModelSha256 not configured — model integrity NOT verified. " +
                "Set expected SHA-256 in appsettings.json to fail-closed on tampered weights.");
            return new ModelVerificationResult(true, _options.ModelPath, null, "present (hash unchecked)");
        }

        try
        {
            var actual = await ComputeSha256Async(_options.ModelPath, ct);
            if (!string.Equals(actual, _options.ModelSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(
                    "Model hash mismatch at {Path}. Expected {Expected} but got {Actual}. Fail-closed.",
                    _options.ModelPath, _options.ModelSha256, actual);
                return new ModelVerificationResult(false, _options.ModelPath, actual,
                    "SHA-256 mismatch — fail-closed");
            }

            _logger.LogInformation("Model verified at {Path} (SHA-256 {Hash})",
                _options.ModelPath, actual);
            return new ModelVerificationResult(true, _options.ModelPath, actual, "verified");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model hash verification failed for {Path}", _options.ModelPath);
            return new ModelVerificationResult(false, _options.ModelPath, null,
                $"hash verification error: {ex.Message}");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
