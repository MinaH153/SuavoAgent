using System.IO.Compression;
using System.Security.Cryptography;

namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// Installs the on-device brain DURING install (the "Installing the brain" phase):
/// downloads the llama.cpp native libs (~1 MB zip) + the Qwen3 GGUF (~1.3 GB),
/// SHA256-verifies both, and lands them exactly where the baked Agent:Reasoning
/// config points — so the agent boots with the brain already present and reports
/// brainReady on its first heartbeat.
///
/// FAIL-SOFT is the contract: any failure cleans partials and returns false; the
/// install always continues (the agent's own provisioners self-heal in the
/// background). The brain phase must never break an install.
/// </summary>
internal static class BrainInstaller
{
    // The GGUF is ~1.3 GB; a pharmacy DSL line at ~2 MB/s needs ~11 min. Bound it
    // so a stalled link can't wedge the installer forever (cancel works regardless).
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Returns true when the brain fully landed (libs extracted + model verified at
    /// its final path). False = skipped/failed (install proceeds; agent self-heals).
    /// Progress reports 0-100 over the combined byte budget (libs + model).
    /// </summary>
    public static async Task<bool> InstallAsync(
        AgentReasoningConfig reasoning,
        string dataDir,
        IProgress<int>? percent,
        CancellationToken ct,
        HttpMessageHandler? handler = null)
    {
        if (!reasoning.IsProvisionable) return false;

        var modelPath = reasoning.GetModelPath(dataDir);
        var nativeDir = reasoning.GetNativeLibsDir(dataDir);
        var modelTmp = modelPath + ".download";

        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = DownloadTimeout;

        try
        {
            // Already present (reinstall over a provisioned box)? Verify + keep.
            if (File.Exists(modelPath) && Directory.Exists(nativeDir)
                && await FileMatchesSha256Async(modelPath, reasoning.ModelSha256, ct))
            {
                percent?.Report(100);
                return true;
            }

            var libsBytes = Math.Max(1, reasoning.NativeLibsSizeBytes ?? 2_000_000);
            var modelBytes = Math.Max(1, reasoning.ModelSizeBytes ?? 1_300_000_000);
            var totalBytes = libsBytes + modelBytes;

            // ── Native libs: download zip → verify SHA → extract ────────────
            Directory.CreateDirectory(nativeDir);
            var libsTmp = Path.Combine(nativeDir, ".libs.zip.download");
            var libsDownloaded = await DownloadWithProgressAsync(
                http, reasoning.NativeLibsUrl, libsTmp, reasoning.NativeLibsSha256,
                done => percent?.Report((int)(done * 100 / totalBytes)), ct);
            if (!libsDownloaded) { Cleanup(libsTmp); return false; }

            ZipFile.ExtractToDirectory(libsTmp, nativeDir, overwriteFiles: true);
            Cleanup(libsTmp);

            // ── Model: download → verify SHA (incremental) → atomic move ────
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            var modelDownloaded = await DownloadWithProgressAsync(
                http, reasoning.ModelUrl, modelTmp, reasoning.ModelSha256,
                done => percent?.Report((int)((libsBytes + done) * 100 / totalBytes)), ct);
            if (!modelDownloaded) { Cleanup(modelTmp); return false; }

            File.Move(modelTmp, modelPath, overwrite: true);
            percent?.Report(100);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Operator cancel: clean partials, let the caller decide flow.
            Cleanup(modelTmp);
            throw;
        }
        catch
        {
            // Fail-soft: network/disk/zip errors never break the install.
            Cleanup(modelTmp);
            return false;
        }
    }

    /// <summary>Streams a download to disk computing SHA256 incrementally (no second
    /// read of a 1.3 GB file). Returns false on any mismatch or HTTP failure.</summary>
    private static async Task<bool> DownloadWithProgressAsync(
        HttpClient http, string url, string destination, string expectedSha256,
        Action<long> onBytes, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return false;

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.AppendData(buffer, 0, read);
                done += read;
                onBytes(done);
            }
        }

        var actual = Convert.ToHexString(sha.GetHashAndReset());
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            Cleanup(destination);
            return false;
        }
        return true;
    }

    private static async Task<bool> FileMatchesSha256Async(string path, string expected, CancellationToken ct)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(fs, ct);
            return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }
}
