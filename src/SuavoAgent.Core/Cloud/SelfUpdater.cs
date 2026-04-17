using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Downloads a new agent binary, verifies ECDSA P-256 signature + SHA256, swaps in-place, exits.
/// Windows service auto-restart policy brings the new binary online.
///
/// Security model:
///   - Private key: stored on Joshua's machine, signs update manifests
///   - Public key: embedded here (ECDSA P-256), verifies signatures
///   - Even if the cloud is fully compromised, attacker cannot push a malicious binary
///     without the private key (which never leaves the signing machine)
/// </summary>
public static class SelfUpdater
{
    // ── Key Rotation Procedure ──
    // The agent accepts signatures from ANY key in the registry.
    // To rotate a signing key:
    //   1. Generate new keypair
    //   2. Add new public key to registry as "update-v2" (or "cmd-v2")
    //   3. Release update signed with OLD key — agents accept it and get the new key
    //   4. Switch CI/CD to sign with NEW key
    //   5. Remove old key from registry in next release
    // During the transition window, agents accept BOTH keys.

    // ECDSA P-256 public key for update manifest verification.
    // Private key: ~/.suavo/signing-key.pem (Joshua's Mac).
    internal const string UpdatePublicKeyDer =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBLRvZ572EpqNab9CxJ9/b/GfHpHOrhWkpaaCzIkXQ5d2dwiqdJHlxvrgN0/zCsgp/ccnDXed4DFCkh6wUWCvWA==";

    // ECDSA P-256 public key for signed control-plane commands (fetch_patient, decommission, update).
    // Separate from update key — compromise of one doesn't grant the other.
    // Private key: ~/.suavo/cmd-signing-key.pem (Joshua's Mac).
    internal const string CommandPublicKeyDer =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE1mIlEiYIEqjp/YBymnFH9FEUxYFXd+Y25cPiF5wdcEo9CP+760IMxHgajrUt9A3zJ47dwV893LWwlZ1/nDP3YA==";

    // ECDSA P-256 public key for verifying seed response bodies (H-11).
    // Uses the same command-signing key — cloud signs seed payloads before returning them.
    // Prevents a compromised cloud from injecting malicious SQL shapes.
    internal const string SeedPublicKeyDer = CommandPublicKeyDer;

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "suavollc.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com"
    };

    public static bool IsAllowedUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == "https" && AllowedHosts.Contains(uri.Host);
    }

    [Obsolete("Use TryApplyPackageUpdateAsync for 3-binary updates")]
    public static async Task<bool> TryApplyUpdateAsync(
        string downloadUrl, string expectedSha256, string version, string? signature,
        ILogger logger, CancellationToken ct)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe))
        {
            logger.LogWarning("Cannot determine current exe path — skipping update");
            return false;
        }

        var installDir = Path.GetDirectoryName(currentExe)!;
        var newExe = Path.Combine(installDir, "SuavoAgent.Core.exe.new");
        var oldExe = Path.Combine(installDir, "SuavoAgent.Core.exe.old");

        try
        {
            // 0a. Validate URL — only HTTPS from trusted domains
            if (!IsAllowedUrl(downloadUrl))
            {
                logger.LogWarning("Untrusted update URL rejected: {Url}", downloadUrl);
                return false;
            }

            // 0b. Verify ECDSA P-256 signature of the update manifest
            if (!VerifyManifestSignature(downloadUrl, expectedSha256, version, signature, logger))
                return false;

            // 1. Download with 200 MB size cap
            logger.LogInformation("Downloading update v{Version} from {Url}", version, downloadUrl);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await DownloadWithSizeLimitAsync(http, downloadUrl, newExe, ct);

            // 2. Verify SHA256
            var actualHash = await ComputeSha256Async(newExe, ct);
            if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("SHA256 mismatch: expected {Expected}, got {Actual} — aborting",
                    expectedSha256, actualHash);
                File.Delete(newExe);
                return false;
            }
            logger.LogInformation("SHA256 verified: {Hash}", actualHash);

            // 3. Swap — Windows allows renaming a running exe
            if (File.Exists(oldExe)) File.Delete(oldExe);
            File.Move(currentExe, oldExe);
            File.Move(newExe, currentExe);
            logger.LogInformation("Binary swapped: v{Version} is now in place", version);

            // 4. Exit with non-zero code — SCM only auto-restarts on failure
            logger.LogInformation("Exiting for restart with new binary...");
            Environment.Exit(1);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Self-update failed — attempting rollback");
            if (!File.Exists(currentExe) && File.Exists(oldExe))
                try { File.Move(oldExe, currentExe); logger.LogInformation("Rolled back to previous binary"); } catch { }
            try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Verifies ECDSA P-256 signature over the manifest.
    /// Manifest format: "{url}|{sha256}|{version}" (pipe-delimited, no newlines).
    /// Signature is hex-encoded in the heartbeat response.
    /// </summary>
    private static bool VerifyManifestSignature(
        string url, string sha256, string version, string? signatureHex, ILogger logger)
    {
        // Reject fields containing pipe or control characters (prevents injection)
        if (ContainsControlChars(url) || ContainsControlChars(sha256) || ContainsControlChars(version))
        {
            logger.LogWarning("Manifest fields contain control characters — rejecting");
            return false;
        }

        var manifestCanonical = $"{url}|{sha256}|{version}";
        return VerifyManifestSignature(manifestCanonical, signatureHex, logger);
    }

    /// <summary>
    /// Verifies ECDSA P-256 signature over a pre-built canonical manifest string.
    /// Used by both legacy single-binary updates and new package-level updates.
    /// </summary>
    internal static bool VerifyManifestSignature(
        string manifestCanonical, string? signatureHex, ILogger logger)
    {
        if (string.IsNullOrEmpty(signatureHex))
        {
            logger.LogWarning("Update manifest has no signature — rejecting");
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePublicKeyDer), out _);

            var manifestBytes = Encoding.UTF8.GetBytes(manifestCanonical);
            var signatureBytes = Convert.FromHexString(signatureHex);

            var valid = ecdsa.VerifyData(manifestBytes, signatureBytes, HashAlgorithmName.SHA256);

            if (!valid)
            {
                logger.LogWarning("Update manifest signature is INVALID — rejecting");
                return false;
            }

            logger.LogInformation("Update manifest signature verified (ECDSA P-256)");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Signature verification failed — rejecting update");
            return false;
        }
    }

    private static bool ContainsControlChars(string s) =>
        s.AsSpan().IndexOfAny('\n', '\r', '|') >= 0;

    /// <summary>
    /// 3-binary package update: downloads Core, Broker, Helper to .exe.new,
    /// verifies each SHA256, writes update-pending.flag sentinel, then exits
    /// so SCM restarts and CheckPendingUpdate finishes the swap.
    /// </summary>
    public static async Task<bool> TryApplyPackageUpdateAsync(
        UpdateManifest manifest, string signatureHex, ILogger logger, CancellationToken ct)
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(installDir))
        {
            logger.LogWarning("Cannot determine install dir — skipping package update");
            return false;
        }

        // Validate manifest signature
        if (!VerifyManifestSignature(manifest.ToCanonical(), signatureHex, logger))
            return false;

        // Validate all URLs
        var downloads = new[]
        {
            (Url: manifest.CoreUrl, Sha256: manifest.CoreSha256, Binary: "SuavoAgent.Core.exe"),
            (Url: manifest.BrokerUrl, Sha256: manifest.BrokerSha256, Binary: "SuavoAgent.Broker.exe"),
            (Url: manifest.HelperUrl, Sha256: manifest.HelperSha256, Binary: "SuavoAgent.Helper.exe"),
        };

        foreach (var d in downloads)
        {
            if (!IsAllowedUrl(d.Url))
            {
                logger.LogWarning("Untrusted URL in manifest for {Binary}: {Url}", d.Binary, d.Url);
                return false;
            }
        }

        // Validate runtime
        if (!manifest.MatchesRuntime("net8.0", "win-x64"))
        {
            logger.LogWarning("Manifest runtime mismatch: {Runtime}/{Arch}", manifest.Runtime, manifest.Arch);
            return false;
        }

        var stagedFiles = new List<string>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

            foreach (var d in downloads)
            {
                var newPath = Path.Combine(installDir, d.Binary + ".new");
                stagedFiles.Add(newPath);

                logger.LogInformation("Downloading {Binary} from {Url}", d.Binary, d.Url);
                await DownloadWithSizeLimitAsync(http, d.Url, newPath, ct);

                var actualHash = await ComputeSha256Async(newPath, ct);
                if (!string.Equals(actualHash, d.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("SHA256 mismatch for {Binary}: expected {Expected}, got {Actual}",
                        d.Binary, d.Sha256, actualHash);
                    CleanupStagedFiles(installDir);
                    return false;
                }
                logger.LogInformation("{Binary} verified: {Hash}", d.Binary, actualHash);
            }

            // Write sentinel: line 1 = manifest canonical, line 2 = signature hex
            var sentinelPath = Path.Combine(installDir, "update-pending.flag");
            await File.WriteAllTextAsync(sentinelPath,
                $"{manifest.ToCanonical()}\n{signatureHex}", ct);

            logger.LogInformation("Staged {Count} binaries, sentinel written — exiting for restart",
                stagedFiles.Count);
            Environment.Exit(1);
            return true; // unreachable
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Package update failed — cleaning up staged files");
            CleanupStagedFiles(installDir);
            return false;
        }
    }

    private static void CleanupStagedFiles(string installDir)
    {
        foreach (var f in Directory.GetFiles(installDir, "*.exe.new"))
            try { File.Delete(f); } catch { }
        var sentinel = Path.Combine(installDir, "update-pending.flag");
        try { if (File.Exists(sentinel)) File.Delete(sentinel); } catch { }
    }

    private const long MaxUpdateBytes = 200 * 1024 * 1024; // 200 MB — matches BinaryDownloader cap

    private static async Task DownloadWithSizeLimitAsync(
        HttpClient http, string url, string destPath, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        if (totalBytes > MaxUpdateBytes)
            throw new InvalidOperationException(
                $"Update binary too large ({totalBytes / (1024 * 1024)} MB > 200 MB limit) — aborting");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(destPath);
        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            if (totalRead > MaxUpdateBytes)
                throw new InvalidOperationException(
                    $"Update binary exceeded 200 MB limit mid-stream — aborting");
        }
    }

    public static void CleanupOldBinaries(ILogger logger)
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(installDir)) return;

        foreach (var oldFile in Directory.GetFiles(installDir, "*.exe.old"))
        {
            try
            {
                File.Delete(oldFile);
                logger.LogInformation("Cleaned up {File}", Path.GetFileName(oldFile));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not delete {File} — will retry next startup", Path.GetFileName(oldFile));
            }
        }
    }

    public static void CleanupOldBinary(ILogger logger)
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(currentExe)) return;

        var oldExe = Path.Combine(Path.GetDirectoryName(currentExe)!, "SuavoAgent.Core.exe.old");
        if (File.Exists(oldExe))
        {
            try
            {
                File.Delete(oldExe);
                logger.LogInformation("Cleaned up old binary from previous update");
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not delete old binary — will retry next startup");
            }
        }
    }

    public static bool SwapBinaries(string installDir, ILogger logger)
    {
        var binaries = new[] { "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe" };
        var swapped = new List<string>();

        try
        {
            foreach (var bin in binaries)
            {
                var current = Path.Combine(installDir, bin);
                var newFile = current + ".new";
                var oldFile = current + ".old";

                if (!File.Exists(newFile)) continue;

                if (File.Exists(oldFile)) File.Delete(oldFile);
                if (File.Exists(current)) File.Move(current, oldFile);
                File.Move(newFile, current);
                swapped.Add(bin);
                logger.LogInformation("Swapped {Binary}", bin);
            }
            return swapped.Count > 0;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Binary swap failed after {Count} swaps — rolling back", swapped.Count);
            foreach (var bin in swapped)
            {
                var current = Path.Combine(installDir, bin);
                var oldFile = current + ".old";
                try
                {
                    if (File.Exists(current)) File.Delete(current);
                    if (File.Exists(oldFile)) File.Move(oldFile, current);
                }
                catch { }
            }
            foreach (var bin in binaries)
            {
                try { var f = Path.Combine(installDir, bin + ".new"); if (File.Exists(f)) File.Delete(f); } catch { }
            }
            return false;
        }
    }

    public static bool CheckPendingUpdate(ILogger logger)
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrEmpty(installDir)) return false;

        var sentinel = Path.Combine(installDir, "update-pending.flag");
        if (!File.Exists(sentinel))
        {
            foreach (var f in Directory.GetFiles(installDir, "*.exe.new"))
                try { File.Delete(f); } catch { }
            return false;
        }

        logger.LogInformation("Found update-pending sentinel — applying update");
        try
        {
            var sentinelData = File.ReadAllText(sentinel);
            var lines = sentinelData.Split('\n', 2);
            if (lines.Length < 2)
            {
                logger.LogWarning("Malformed sentinel — discarding");
                File.Delete(sentinel);
                return false;
            }

            var manifestCanonical = lines[0].Trim();
            var signatureHex = lines[1].Trim();

            var manifest = UpdateManifest.Parse(manifestCanonical);
            if (manifest == null)
            {
                logger.LogWarning("Cannot parse manifest from sentinel — discarding");
                File.Delete(sentinel);
                return false;
            }

            if (!VerifyManifestSignature(manifest.ToCanonical(), signatureHex, logger))
            {
                logger.LogWarning("Sentinel manifest signature invalid — discarding update");
                File.Delete(sentinel);
                foreach (var f in Directory.GetFiles(installDir, "*.exe.new"))
                    try { File.Delete(f); } catch { }
                return false;
            }

            // Re-verify staged binary hashes before swap — closes TOCTOU window
            // between download-time hash check and restart-time swap
            if (!VerifyStagedBinaries(installDir, manifest, logger))
            {
                logger.LogWarning("Staged binary re-verification failed — discarding update");
                File.Delete(sentinel);
                foreach (var f in Directory.GetFiles(installDir, "*.exe.new"))
                    try { File.Delete(f); } catch { }
                return false;
            }

            if (SwapBinaries(installDir, logger))
            {
                File.Delete(sentinel);
                logger.LogInformation("Bootstrap update applied — v{Version}", manifest.Version);
                return true;
            }

            logger.LogWarning("Binary swap failed during bootstrap update");
            File.Delete(sentinel);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bootstrap update check failed");
            try { File.Delete(sentinel); } catch { }
            return false;
        }
    }

    private static bool VerifyStagedBinaries(string installDir, UpdateManifest manifest, ILogger logger)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SuavoAgent.Core.exe"]   = manifest.CoreSha256,
            ["SuavoAgent.Broker.exe"] = manifest.BrokerSha256,
            ["SuavoAgent.Helper.exe"] = manifest.HelperSha256,
        };

        foreach (var (binary, expectedHash) in expected)
        {
            var newPath = Path.Combine(installDir, binary + ".new");
            if (!File.Exists(newPath)) continue;

            using var stream = File.OpenRead(newPath);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Re-verification mismatch for {Binary}: expected {Expected}, got {Actual}",
                    binary, expectedHash, actual);
                return false;
            }
        }
        return true;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
