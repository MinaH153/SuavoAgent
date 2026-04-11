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
    // ECDSA P-256 public key (DER SubjectPublicKeyInfo, Base64).
    // Private key stored in macOS Keychain / secure vault.
    // Internal so HeartbeatWorker can reuse for signed command verification.
    internal const string UpdatePublicKeyDer =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJJO30pUIre7wuMN5I1FQmlEDpTIM0dmhPjaGtlG7gm+47G7lKHuJV4lQ3eWhZNqe1eviOZkt+9VnWnQUSJGvsg==";

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

            // 1. Download
            logger.LogInformation("Downloading update v{Version} from {Url}", version, downloadUrl);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            await using var stream = await http.GetStreamAsync(downloadUrl, ct);
            await using var file = File.Create(newExe);
            await stream.CopyToAsync(file, ct);
            file.Close();

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

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
