using System.IO.Compression;
using System.Security.Cryptography;

namespace SuavoAgent.Core.Vision;

/// <summary>
/// Downloads the SHA-256-verified Tesseract native bundle (leptonica + tesseract50 + eng.traineddata)
/// and extracts it into the agent's vision dir. Runs in CORE (SYSTEM) — the only context that can
/// write the ACL-locked %ProgramData%\SuavoAgent folder, so the Helper (interactive session) can then
/// READ the binaries. Native code, so the SHA is mandatory (enforced by the caller). Fail-soft: returns
/// a result, never throws out (except cancellation).
/// </summary>
public sealed class TesseractBundleProvisioner
{
    private readonly ILogger _logger;

    public TesseractBundleProvisioner(ILogger logger) => _logger = logger;

    public sealed record Result(bool Ok, string Message);

    /// <summary>
    /// Ensures tesseract50.dll + tessdata/eng.traineddata exist under <paramref name="targetDir"/>,
    /// downloading + verifying + extracting the bundle zip if not. Idempotent.
    /// </summary>
    public async Task<Result> ProvisionAsync(
        string bundleUrl, string sha256, string targetDir, CancellationToken ct, HttpMessageHandler? handler = null)
    {
        var dll = Path.Combine(targetDir, "tesseract50.dll");
        var traineddata = Path.Combine(targetDir, "tessdata", "eng.traineddata");
        try
        {
            if (File.Exists(dll) && File.Exists(traineddata))
                return new Result(true, "already provisioned");

            Directory.CreateDirectory(targetDir);
            var tmp = Path.Combine(targetDir, ".tess-bundle.zip.download");

            using var http = handler is null ? new HttpClient() : new HttpClient(handler);
            http.Timeout = TimeSpan.FromMinutes(20);
            // HuggingFace/GitHub redirect/UA behaviour — a UA avoids a 1 KB error page in place of the file.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SuavoAgent/1.0 (+https://suavollc.com)");

            using (var resp = await http.GetAsync(bundleUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    return new Result(false, $"bundle download HTTP {(int)resp.StatusCode}");

                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using (var src = await resp.Content.ReadAsStreamAsync(ct))
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                {
                    var buffer = new byte[1 << 20];
                    int read;
                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        sha.AppendData(buffer, 0, read);
                    }
                }

                // Native code — the SHA is mandatory. Verify BEFORE extracting: a corrupt/tampered
                // bundle must never land executable DLLs on the box.
                var actual = Convert.ToHexString(sha.GetHashAndReset());
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tmp);
                    return new Result(false, $"bundle SHA-256 mismatch (got {actual})");
                }
            }

            ZipFile.ExtractToDirectory(tmp, targetDir, overwriteFiles: true);
            TryDelete(tmp);

            if (!File.Exists(dll) || !File.Exists(traineddata))
                return new Result(false, "bundle extracted but tesseract50.dll / eng.traineddata missing");

            _logger.LogInformation("Tesseract bundle provisioned at {Dir}", targetDir);
            return new Result(true, "provisioned");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tesseract bundle provisioning failed");
            return new Result(false, $"provision error: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
