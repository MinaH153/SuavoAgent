using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Auto-provisions the llama.cpp NATIVE libraries (llama.dll + ggml*.dll + llava_shared.dll) into
/// <see cref="ReasoningOptions.NativeLibraryPath"/> on first run. They are deliberately NOT shipped in
/// the installer (stealth — their presence is a "vendor fingerprint", SuavoAgent.Core.csproj), so a
/// reasoning-enabled box self-equips by downloading a small ZIP of ONE AVX variant from
/// <see cref="ReasoningOptions.NativeLibsUrl"/>, SHA256-verifying it, and extracting the DLLs flat.
///
/// Fail-soft: any failure leaves the libs absent → <see cref="LLamaLocalInference"/> fails its
/// pre-flight and reasoning stays off. NEVER blocks the agent or competes with PioneerRx (background,
/// one-time). The 4 required DLLs are llama.dll, ggml.dll, ggml-base.dll, ggml-cpu.dll (llava optional).
/// </summary>
public sealed class NativeLibProvisioner
{
    private static readonly string[] RequiredDlls = { "llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll" };
    private static int _downloadStarted;

    private readonly ReasoningOptions _options;
    private readonly ILogger<NativeLibProvisioner> _logger;

    public NativeLibProvisioner(IOptions<AgentOptions> agentOptions, ILogger<NativeLibProvisioner> logger)
    {
        _options = agentOptions.Value.Reasoning;
        _logger = logger;
    }

    public bool DllsPresent()
    {
        var dir = _options.NativeLibraryPath;
        if (string.IsNullOrWhiteSpace(dir)) return false;
        return RequiredDlls.All(d => File.Exists(Path.Combine(dir, d)));
    }

    /// <summary>
    /// True when the native DLLs are present. When absent + a URL is set, kicks a one-time BACKGROUND
    /// download (activates on the next restart) and returns false. Mirrors the model provisioner so the
    /// startup factory can short-circuit to rules-only this session without blocking boot.
    /// </summary>
    public bool EnsureOrProvision()
    {
        if (DllsPresent()) return true;
        if (string.IsNullOrWhiteSpace(_options.NativeLibraryPath) || string.IsNullOrWhiteSpace(_options.NativeLibsUrl))
        {
            _logger.LogWarning("Native libs absent and no NativeLibsUrl/Path to provision — reasoning stays off.");
            return false;
        }
        if (Interlocked.CompareExchange(ref _downloadStarted, 1, 0) == 0)
        {
            _ = Task.Run(() => TryDownloadAndExtractAsync(CancellationToken.None));
            _logger.LogInformation("Native libs auto-provisioning in background — activates on next restart.");
        }
        return false;
    }

    private async Task TryDownloadAndExtractAsync(CancellationToken ct)
    {
        var dir = _options.NativeLibraryPath!;
        var tmpZip = Path.Combine(Path.GetTempPath(), $"suavo-native-{Guid.NewGuid():N}.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"suavo-native-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            _logger.LogInformation("Auto-provisioning native libs from {Url} → {Path}", _options.NativeLibsUrl, dir);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using (var response = await http.GetAsync(_options.NativeLibsUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Native libs download HTTP {Code}", (int)response.StatusCode);
                    return;
                }
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tmpZip, FileMode.Create, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, 1 << 20, ct);
            }

            if (!string.IsNullOrWhiteSpace(_options.NativeLibsSha256))
            {
                var actual = await ComputeSha256Async(tmpZip, ct);
                if (!string.Equals(actual, _options.NativeLibsSha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Native libs SHA-256 mismatch (got {Actual}) — refusing to extract.", actual);
                    return;
                }
            }
            else
            {
                _logger.LogWarning("NativeLibsSha256 not set — native libs integrity NOT verified.");
            }

            // Extract to a temp dir, confirm the required DLLs are there, THEN copy into place — a
            // partial/garbage extract must never become the live native dir.
            Directory.CreateDirectory(tmpDir);
            ZipFile.ExtractToDirectory(tmpZip, tmpDir, overwriteFiles: true);
            if (!RequiredDlls.All(d => File.Exists(Path.Combine(tmpDir, d))))
            {
                _logger.LogError("Native libs ZIP missing required DLLs after extract — aborting.");
                return;
            }
            foreach (var file in Directory.GetFiles(tmpDir, "*.dll"))
            {
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            }
            _logger.LogInformation("Native libs provisioned at {Path} ({Count} DLLs)", dir, Directory.GetFiles(dir, "*.dll").Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Native libs auto-provision failed (reasoning stays off; agent unaffected)");
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
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
