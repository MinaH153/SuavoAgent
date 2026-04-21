using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Attestation;

/// <summary>
/// End-to-end attestation check: fetch signed manifest → verify signature →
/// hash local files → compare. Mismatch triggers halt signal.
/// </summary>
public sealed class AttestationVerifier : IAttestationVerifier
{
    private readonly IManifestFetcher _fetcher;
    private readonly IManifestSignatureVerifier _signatureVerifier;
    private readonly IFileHasher _fileHasher;
    private readonly IAttestationHaltSignal _haltSignal;
    private readonly ILogger<AttestationVerifier> _logger;
    private readonly string _installDir;
    private readonly string _agentVersion;

    public AttestationVerifier(
        IManifestFetcher fetcher,
        IManifestSignatureVerifier signatureVerifier,
        IFileHasher fileHasher,
        IAttestationHaltSignal haltSignal,
        ILogger<AttestationVerifier> logger,
        string installDir,
        string agentVersion)
    {
        _fetcher = fetcher;
        _signatureVerifier = signatureVerifier;
        _fileHasher = fileHasher;
        _haltSignal = haltSignal;
        _logger = logger;
        _installDir = installDir;
        _agentVersion = agentVersion;
    }

    public async Task<AttestationResult> VerifyAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        if (!Directory.Exists(_installDir))
            return AttestationResult.ConfigurationError($"install_dir_missing: {_installDir}");

        var payload = await _fetcher.FetchAsync(_agentVersion, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            stopwatch.Stop();
            _logger.LogWarning("attestation: network failure fetching manifest for {Version}", _agentVersion);
            return AttestationResult.NetworkFailure(
                "could not fetch manifest",
                stopwatch.ElapsedMilliseconds);
        }

        if (!_signatureVerifier.Verify(payload.ManifestJson, payload.SignatureBytes))
        {
            stopwatch.Stop();
            _logger.LogCritical("attestation: manifest signature verification FAILED for {Version}", _agentVersion);
            _haltSignal.Halt($"manifest signature invalid for version {_agentVersion}");
            return AttestationResult.SignatureInvalid(
                "manifest signature verification failed",
                stopwatch.ElapsedMilliseconds);
        }

        AttestationManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AttestationManifest>(payload.ManifestJson);
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "attestation: manifest JSON deserialization failed");
            return AttestationResult.ConfigurationError($"manifest_json_invalid: {ex.Message}");
        }

        if (manifest is null)
            return AttestationResult.ConfigurationError("manifest_null_after_deserialize");

        if (!string.Equals(manifest.Version, _agentVersion, StringComparison.Ordinal))
            return AttestationResult.ConfigurationError(
                $"version_mismatch: manifest={manifest.Version} agent={_agentVersion}");

        // Hash each expected file and compare.
        var mismatches = new List<FileMismatch>();
        foreach (var expected in manifest.Files)
        {
            var path = Path.Combine(_installDir, expected.Name);
            var actual = _fileHasher.Sha256(path);
            if (actual is null)
            {
                mismatches.Add(new FileMismatch(expected.Name, expected.Sha256, null, "file_missing"));
                continue;
            }
            if (!string.Equals(actual, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(new FileMismatch(expected.Name, expected.Sha256, actual, "hash_mismatch"));
            }
        }

        stopwatch.Stop();

        if (mismatches.Count > 0)
        {
            _logger.LogCritical(
                "attestation MISMATCH for {Version} — {Count} file(s): {Files}",
                manifest.Version, mismatches.Count,
                string.Join(", ", mismatches.Select(m => m.FileName)));
            _haltSignal.Halt(
                $"attestation mismatch: {mismatches.Count} file(s) differ from signed manifest v{manifest.Version}");
            return AttestationResult.Mismatch(manifest.Version, mismatches, stopwatch.ElapsedMilliseconds);
        }

        _logger.LogInformation(
            "attestation verified for {Version} — {Count} files, {Duration}ms",
            manifest.Version, manifest.Files.Count, stopwatch.ElapsedMilliseconds);
        return AttestationResult.Verified(manifest.Version, stopwatch.ElapsedMilliseconds);
    }
}
