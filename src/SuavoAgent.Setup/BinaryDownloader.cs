using System.Security.Cryptography;

namespace SuavoAgent.Setup;

/// <summary>
/// Downloads agent binaries from GitHub release, verifies ECDSA signature on checksums,
/// then verifies SHA-256 of each binary.
/// </summary>
internal static class BinaryDownloader
{
    // Hardcoded repo coordinates — never read from config (C-1 security fix)
    internal const string RepoOwner = "MinaH153";
    internal const string RepoName = "SuavoAgent";

    // ECDSA P-256 public key for checksum signature verification (DER/SubjectPublicKeyInfo, Base64)
    // Matches the private key at ~/.suavo/update-signing-p256.pem
    private const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBLRvZ572EpqNab9CxJ9/b/GfHpHOrhWkpaaCzIkXQ5d2dwiqdJHlxvrgN0/zCsgp/ccnDXed4DFCkh6wUWCvWA==";

    private static readonly string[] Binaries =
    [
        "SuavoAgent.Core.exe",
        "SuavoAgent.Broker.exe",
        "SuavoAgent.Helper.exe",
    ];

    /// Maximum download size per binary (200 MB). Aborts if Content-Length exceeds this (H-4).
    private const long MaxDownloadBytes = 200 * 1024 * 1024;

    /// <summary>
    /// Downloads, verifies, and installs all agent binaries. Returns true on success.
    /// </summary>
    public static async Task<bool> DownloadAndVerifyAsync(string releaseTag, string installDir)
    {
        var baseUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{releaseTag}";

        Directory.CreateDirectory(installDir);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SuavoSetup/1.0");

        // Step 1: Download and verify checksums
        var checksums = await DownloadAndVerifyChecksumsAsync(http, baseUrl, installDir);
        if (checksums == null) return false;

        // Step 2: Verify all expected binaries have checksum entries
        foreach (var bin in Binaries)
        {
            if (!checksums.ContainsKey(bin))
            {
                ConsoleUI.WriteFail($"Checksum missing for {bin} - aborting");
                return false;
            }
        }

        // Step 3: Download each binary with progress
        foreach (var bin in Binaries)
        {
            var url = $"{baseUrl}/{bin}";
            var destPath = Path.Combine(installDir, bin);

            ConsoleUI.WriteInfo($"Downloading {bin}...");

            if (!await DownloadFileAsync(http, url, destPath, bin))
                return false;

            // Verify SHA-256
            var actualHash = ComputeSha256(destPath);
            if (!actualHash.Equals(checksums[bin], StringComparison.OrdinalIgnoreCase))
            {
                ConsoleUI.WriteFail($"SHA-256 mismatch for {bin}");
                ConsoleUI.WriteInfo($"  Expected: {checksums[bin]}");
                ConsoleUI.WriteInfo($"  Actual:   {actualHash}");
                CleanupBinaries(installDir);
                return false;
            }

            var sizeMb = new FileInfo(destPath).Length / (1024.0 * 1024.0);
            ConsoleUI.WriteOk($"{bin} verified ({sizeMb:F1} MB)");
        }

        return true;
    }

    /// <summary>
    /// Downloads checksums.sha256 and checksums.sha256.sig, verifies ECDSA signature,
    /// parses the checksum file into a dictionary.
    /// </summary>
    private static async Task<Dictionary<string, string>?> DownloadAndVerifyChecksumsAsync(
        HttpClient http, string baseUrl, string installDir)
    {
        var checksumPath = Path.Combine(installDir, "checksums.sha256");
        var sigPath = Path.Combine(installDir, "checksums.sha256.sig");

        ConsoleUI.WriteInfo("Downloading checksums...");

        try
        {
            var checksumBytes = await http.GetByteArrayAsync($"{baseUrl}/checksums.sha256");
            await File.WriteAllBytesAsync(checksumPath, checksumBytes);

            var sigText = (await http.GetStringAsync($"{baseUrl}/checksums.sha256.sig")).Trim();
            await File.WriteAllTextAsync(sigPath, sigText);

            // Verify ECDSA signature
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);

            var sigBytes = Convert.FromHexString(sigText);
            var valid = ecdsa.VerifyData(checksumBytes, sigBytes, HashAlgorithmName.SHA256);

            if (!valid)
            {
                ConsoleUI.WriteFail("CRITICAL: Checksum signature verification FAILED - aborting");
                Cleanup(checksumPath, sigPath);
                return null;
            }

            ConsoleUI.WriteOk("Checksum signature verified (ECDSA P-256)");

            // Parse checksums: "hash  filename" per line
            var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var checksumText = System.Text.Encoding.UTF8.GetString(checksumBytes);
            foreach (var line in checksumText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split("  ", 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                    checksums[parts[1]] = parts[0];
            }

            return checksums;
        }
        catch (HttpRequestException ex)
        {
            ConsoleUI.WriteFail($"Download failed: {ex.Message}");
            Cleanup(checksumPath, sigPath);
            return null;
        }
    }

    /// <summary>
    /// Downloads a file with progress reporting.
    /// </summary>
    private static async Task<bool> DownloadFileAsync(
        HttpClient http, string url, string destPath, string label)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            // H-4: Abort if declared size exceeds 200 MB
            if (totalBytes > MaxDownloadBytes)
            {
                ConsoleUI.WriteFail($"{label} too large ({totalBytes / (1024 * 1024)} MB > 200 MB limit) — aborting");
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(destPath);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                // Enforce size limit mid-stream — server may omit Content-Length
                if (totalRead > MaxDownloadBytes)
                {
                    ConsoleUI.WriteFail($"{label} exceeded {MaxDownloadBytes / (1024 * 1024)} MB limit mid-stream — aborting");
                    return false;
                }

                if (totalBytes > 0)
                    ConsoleUI.WriteProgress(label, totalRead, totalBytes);
            }

            return true;
        }
        catch (Exception ex)
        {
            ConsoleUI.WriteFail($"Download failed for {label}: {ex.Message}");
            return false;
        }
    }

    private static string ComputeSha256(string filePath)
    {
        var bytes = File.ReadAllBytes(filePath);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void CleanupBinaries(string installDir)
    {
        foreach (var bin in Binaries)
        {
            var path = Path.Combine(installDir, bin);
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
