using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Auto-provisions the local model so the brain ships with a CLIENT install: if the GGUF is absent at
/// <see cref="ReasoningOptions.ModelPath"/> and a <see cref="ReasoningOptions.ModelUrl"/> is set, it
/// downloads it once (streamed to a temp file, SHA256-verified, atomically moved into place), then
/// verifies as usual. When no URL is set it behaves exactly like the legacy verify-an-operator-placed-
/// file path.
///
/// Fail-soft is the rule: a download/verify failure returns not-ok so reasoning simply stays off — it
/// NEVER blocks the agent from starting or competes with PioneerRx. Trust &gt; a chat feature.
/// </summary>
public sealed class HttpModelProvisioner : IModelManager
{
    private readonly ReasoningOptions _options;
    private readonly ILogger<HttpModelProvisioner> _logger;
    // One background download per process — guards against a re-verify kicking off a second pull.
    private static int _downloadStarted;

    public HttpModelProvisioner(IOptions<AgentOptions> agentOptions, ILogger<HttpModelProvisioner> logger)
    {
        _options = agentOptions.Value.Reasoning;
        _logger = logger;
    }

    public async Task<ModelVerificationResult> VerifyAsync(CancellationToken ct)
    {
        var publisher = ValidatePublisher();
        if (!publisher.IsValid)
            return new ModelVerificationResult(
                false,
                _options.ModelPath,
                null,
                $"publisher authorization rejected ({publisher.Code})");

        if (string.IsNullOrWhiteSpace(_options.ModelPath))
            return new ModelVerificationResult(false, null, null, "ModelPath not configured");

        if (!File.Exists(_options.ModelPath))
        {
            if (string.IsNullOrWhiteSpace(_options.ModelUrl))
                return new ModelVerificationResult(false, _options.ModelPath, null,
                    $"Model file missing at {_options.ModelPath} and no ModelUrl to auto-provision");

            // A multi-GB pull must NOT block service start (the startup verify has a ~2-min budget) and
            // must NOT compete with PioneerRx during the boot rush. Kick it off in the BACKGROUND on its
            // own long-lived token; the model activates on the next restart. This session stays
            // rules-only. Fail-soft by construction.
            if (Interlocked.CompareExchange(ref _downloadStarted, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    var (ok, _) = await TryDownloadAsync(CancellationToken.None);
                    // Release the once-per-process latch on failure so a later VerifyAsync re-call
                    // (the deferred wrapper re-kicks it) starts a fresh download instead of the brain
                    // staying off until a full process restart.
                    if (!ok) Interlocked.Exchange(ref _downloadStarted, 0);
                });
                return new ModelVerificationResult(false, _options.ModelPath, null,
                    "model auto-provisioning in background — activates on next restart");
            }
            return new ModelVerificationResult(false, _options.ModelPath, null,
                "model auto-provisioning in progress");
        }

        return await VerifyExistingAsync(ct);
    }

    private async Task<(bool ok, string message)> TryDownloadAsync(CancellationToken ct)
    {
        var publisher = ValidatePublisher();
        if (!publisher.IsValid)
            return (false, $"publisher authorization rejected ({publisher.Code})");

        var dest = _options.ModelPath!;
        var tmp = dest + ".download";
        try
        {
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _logger.LogInformation("core.model.download_started");

            // A GGUF is GBs over a pharmacy's connection — generous timeout, streamed to disk so we
            // never hold the whole file in memory on an 8 GB box.
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
            // HuggingFace (a primary GGUF host) returns a ~1 KB HTML error page instead of the file
            // when there's no User-Agent — the download then fails the SHA check and the brain never
            // provisions (observed on a live box: empty models dir, ready=false). A UA makes HF-direct
            // model hosting work, not just GitHub. Harmless for GitHub/other hosts.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SuavoAgent/1.0 (+https://suavollc.com)");
            using var response = await http.GetAsync(_options.ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return (false, $"model download HTTP {(int)response.StatusCode}");

            var expectedSize = _options.ModelSizeBytes!.Value;
            if (response.Content.Headers.ContentLength is long contentLength &&
                contentLength != expectedSize)
                return (false, "model download declared size mismatch");

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(
                             tmp,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             1 << 20,
                             FileOptions.WriteThrough))
            {
                var buffer = new byte[1 << 20];
                long written = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    if (written > expectedSize - read)
                    {
                        TryDelete(tmp);
                        return (false, "model download exceeded signed size");
                    }
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    written += read;
                }
                await dst.FlushAsync(ct);
                dst.Flush(flushToDisk: true);
                if (written != expectedSize)
                {
                    TryDelete(tmp);
                    return (false, "model download size mismatch");
                }
            }

            // Verify the temp file BEFORE moving it into place — a corrupt/tampered download must never
            // become the live model.
            if (!string.IsNullOrWhiteSpace(_options.ModelSha256))
            {
                var actual = await ComputeSha256Async(tmp, ct);
                if (!string.Equals(actual, _options.ModelSha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tmp);
                    return (false, $"downloaded model SHA-256 mismatch (got {actual})");
                }
            }
            else
            {
                _logger.LogWarning("ModelSha256 not set — downloaded model integrity NOT verified.");
            }

            File.Move(tmp, dest, overwrite: false);
            _logger.LogInformation("core.model.provisioned");
            return (true, "downloaded");
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            _logger.LogSafeWarning(ex);
            return (false, $"model_download_exception:{ex.GetType().Name}");
        }
    }

    private async Task<ModelVerificationResult> VerifyExistingAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ModelSha256))
        {
            _logger.LogWarning("ReasoningOptions.ModelSha256 not configured — model integrity NOT verified.");
            return new ModelVerificationResult(true, _options.ModelPath, null, "present (hash unchecked)");
        }
        try
        {
            if (new FileInfo(_options.ModelPath!).Length != _options.ModelSizeBytes)
                return new ModelVerificationResult(
                    false,
                    _options.ModelPath,
                    null,
                    "signed model size mismatch — fail-closed");
            var actual = await ComputeSha256Async(_options.ModelPath!, ct);
            if (!string.Equals(actual, _options.ModelSha256, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("core.model.hash_mismatch");
                return new ModelVerificationResult(false, _options.ModelPath, actual, "SHA-256 mismatch — fail-closed");
            }
            _logger.LogInformation("core.model.verified");
            return new ModelVerificationResult(true, _options.ModelPath, actual, "verified");
        }
        catch (Exception ex)
        {
            _logger.LogSafeError(ex);
            return new ModelVerificationResult(
                false,
                _options.ModelPath,
                null,
                $"model_hash_verification_exception:{ex.GetType().Name}");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private SuavoAgent.Contracts.Reasoning.BrainCohortValidationResult ValidatePublisher() =>
        _options.ValidatePublisherInstallation(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent"),
            DateTimeOffset.UtcNow);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
