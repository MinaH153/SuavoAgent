using System.IO.Compression;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Auto-provisions the llama.cpp NATIVE libraries (llama.dll + ggml*.dll + llava_shared.dll) into
/// <see cref="ReasoningOptions.NativeLibraryPath"/> on first run. They are deliberately NOT shipped in
/// the installer (stealth — their presence is a "vendor fingerprint", SuavoAgent.Core.csproj), so a
/// reasoning-enabled box self-equips by downloading a small ZIP of ONE AVX variant, SHA256-verifying it,
/// and extracting the DLLs flat.
///
/// AVX variant: prefers the AVX2 build (<see cref="ReasoningOptions.NativeLibsUrlAvx2"/>) when the CPU
/// reports <see cref="Avx2.IsSupported"/> (~5-10x faster inference), else the universal NOAVX build
/// (<see cref="ReasoningOptions.NativeLibsUrl"/>). Only ggml-cpu.dll actually differs between the two.
/// A <c>.variant</c> marker records which build is on disk so a box can be re-provisioned across an
/// upgrade (noavx → avx2) without a reinstall — the brain keeps running on the old variant while the
/// new one downloads in the background, then swaps in before the next inference (or next restart).
///
/// Fail-soft: any failure leaves the libs absent / on the prior variant → reasoning still runs (or stays
/// off if bare). NEVER blocks the agent or competes with PioneerRx (background, one-time). The 4 required
/// DLLs are llama.dll, ggml.dll, ggml-base.dll, ggml-cpu.dll (llava optional).
/// </summary>
public sealed class NativeLibProvisioner
{
    private const string NoAvx = "noavx";
    private const string Avx2Variant = "avx2";
    private const string VariantMarkerName = ".variant";
    private static readonly string[] RequiredDlls = { "llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll" };
    private static int _downloadStarted;
    // QA I4: after a FAILED provision, the in-flight guard is cleared only once this cooldown elapses,
    // so a transient failure (network / SHA mismatch) recovers on a later attempt without a service
    // restart, while a persistent failure can't storm-retry (the guard stays set during the cooldown).
    private static readonly TimeSpan ProvisionRetryCooldown = TimeSpan.FromMinutes(5);

    private readonly ReasoningOptions _options;
    private readonly ILogger<NativeLibProvisioner> _logger;

    public NativeLibProvisioner(IOptions<AgentOptions> agentOptions, ILogger<NativeLibProvisioner> logger)
    {
        _options = agentOptions.Value.Reasoning;
        _logger = logger;
    }

    /// <summary>The variant we WANT on this box: AVX2 when the CPU supports it AND an AVX2 zip is
    /// configured, else NOAVX (the universal fallback). CPU capability can't change at runtime.</summary>
    internal string DesiredVariant =>
        Avx2.IsSupported
        && !string.IsNullOrWhiteSpace(_options.NativeLibsUrlAvx2)
        && !string.IsNullOrWhiteSpace(_options.NativeLibsSha256Avx2)
            ? Avx2Variant
            : NoAvx;

    private (string? Url, string? Sha) ResolveVariant(string variant) =>
        variant == Avx2Variant
            ? (_options.NativeLibsUrlAvx2, _options.NativeLibsSha256Avx2)
            : (_options.NativeLibsUrl, _options.NativeLibsSha256);

    public bool DllsPresent()
    {
        var dir = _options.NativeLibraryPath;
        if (string.IsNullOrWhiteSpace(dir)) return false;
        return RequiredDlls.All(d => File.Exists(Path.Combine(dir, d)));
    }

    private string? ReadVariantMarker()
    {
        var dir = _options.NativeLibraryPath;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var path = Path.Combine(dir, VariantMarkerName);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim().ToLowerInvariant() : null; }
        catch { return null; }
    }

    /// <summary>
    /// True when the RIGHT variant's DLLs are already on disk. A legacy box with no marker is treated as
    /// NOAVX (the historical default), so we DON'T needlessly re-download noavx — but an unmarked box that
    /// now WANTS avx2 is treated as needing the upgrade.
    /// </summary>
    internal bool CorrectVariantPresent()
    {
        if (!DllsPresent()) return false;
        var marker = ReadVariantMarker();
        return DesiredVariant == Avx2Variant
            ? marker == Avx2Variant
            : marker is null or NoAvx;
    }

    /// <summary>
    /// True when usable native DLLs are present for THIS session. When the wrong variant (or none) is on
    /// disk + a URL is set, kicks a one-time BACKGROUND download of the desired variant (it swaps in once
    /// the in-place DLLs are overwritten before first inference, else on the next restart). Returns true
    /// whenever ANY usable DLLs already exist so the brain stays UP on the current variant while the better
    /// one downloads — only a truly bare box (no DLLs at all) returns false (rules-only this session).
    /// </summary>
    public bool EnsureOrProvision()
    {
        if (CorrectVariantPresent()) return true;

        var desired = DesiredVariant;
        var (url, sha) = ResolveVariant(desired);
        var haveDlls = DllsPresent();

        if (string.IsNullOrWhiteSpace(_options.NativeLibraryPath) || string.IsNullOrWhiteSpace(url))
        {
            if (haveDlls) return true; // can't provision the desired variant, but the current DLLs still work
            _logger.LogWarning("Native libs absent and no NativeLibsUrl/Path to provision — reasoning stays off.");
            return false;
        }

        if (Interlocked.CompareExchange(ref _downloadStarted, 1, 0) == 0)
        {
            _ = Task.Run(() => TryDownloadAndExtractAsync(desired, url!, sha, CancellationToken.None));
            _logger.LogInformation(
                "Native libs {Action} variant '{Variant}' in background — activates on first inference or next restart.",
                haveDlls ? "re-provisioning to" : "auto-provisioning",
                desired);
        }
        return haveDlls; // brain stays up on the current libs while the desired variant downloads
    }

    private async Task TryDownloadAndExtractAsync(string variant, string url, string? expectedSha, CancellationToken ct)
    {
        var dir = _options.NativeLibraryPath!;
        var tmpZip = Path.Combine(Path.GetTempPath(), $"suavo-native-{Guid.NewGuid():N}.zip");
        var tmpDir = Path.Combine(Path.GetTempPath(), $"suavo-native-{Guid.NewGuid():N}");
        var provisionFailed = false;
        try
        {
            Directory.CreateDirectory(dir);
            _logger.LogInformation("Auto-provisioning '{Variant}' native libs from {Url} → {Path}", variant, url, dir);

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
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

            // These DLLs get LOADED INTO THE PROCESS — integrity verification is MANDATORY, never
            // best-effort (security review). sha256-pinning is the control: Authenticode doesn't apply
            // (community-built llama.cpp binaries are unsigned). No expected hash → refuse to extract.
            if (string.IsNullOrWhiteSpace(expectedSha))
            {
                _logger.LogError("NativeLibsSha256 not configured for '{Variant}' — refusing to extract UNVERIFIED native code.", variant);
                return;
            }
            var actual = await ComputeSha256Async(tmpZip, ct);
            if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError("Native libs SHA-256 mismatch for '{Variant}' (got {Actual}) — refusing to extract.", variant, actual);
                return;
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
                // overwrite may throw if a DLL is already memory-mapped by a live inference (Windows locks
                // loaded modules). That's fine — the swap then lands on the next restart, where the
                // provisioner runs before any inference loads the libs. The marker (written last) is what
                // keeps a partial swap from being mistaken for a completed variant change.
                File.Copy(file, Path.Combine(dir, Path.GetFileName(file)), overwrite: true);
            }
            await File.WriteAllTextAsync(Path.Combine(dir, VariantMarkerName), variant, ct);
            _logger.LogInformation("Native libs provisioned ('{Variant}') at {Path} ({Count} DLLs)", variant, dir, Directory.GetFiles(dir, "*.dll").Length);
        }
        catch (Exception ex)
        {
            provisionFailed = true;
            _logger.LogWarning(ex, "Native libs auto-provision failed (reasoning stays off / on prior variant; agent unaffected)");
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); } catch { }
        }

        // QA I4: clear the in-flight guard after a FAILED provision so a later EnsureOrProvision can
        // re-attempt — but hold it for ProvisionRetryCooldown first so a persistent failure (no network /
        // SHA mismatch) can't storm-retry. Success leaves the guard set; the provisioned variant's marker
        // makes EnsureOrProvision short-circuit, so no re-download happens on the happy path.
        if (provisionFailed)
        {
            try { await Task.Delay(ProvisionRetryCooldown, ct); } catch { /* shutting down */ }
            Interlocked.Exchange(ref _downloadStarted, 0);
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
