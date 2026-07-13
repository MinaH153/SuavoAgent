using System.Security.Cryptography;
using SuavoAgent.Contracts.Maintenance;

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
    // Matches the mode-600 offline private key at ~/.suavo/signing-key.pem.
    private const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEBLRvZ572EpqNab9CxJ9/b/GfHpHOrhWkpaaCzIkXQ5d2dwiqdJHlxvrgN0/zCsgp/ccnDXed4DFCkh6wUWCvWA==";

    // ONE list of truth for every runtime executable Setup must download. ServiceInstaller
    // refuses to register ANY service when a service binary is absent from the install
    // dir — the 2026-06-10 fresh-install brick was exactly this list missing
    // Watchdog.exe (published in the release, never downloaded) while the GUI still
    // reported "Installation complete". InstalledCohort adds the locally staged signed
    // maintenance host when the immutable on-disk manifest is written.
    private static readonly string[] Binaries =
    [
        "SuavoAgent.Core.exe",
        "SuavoAgent.Broker.exe",
        "SuavoAgent.Helper.exe",
        "SuavoAgent.Watchdog.exe",
    ];

    /// <summary>Test seam — the canonical set of executables Setup downloads and verifies.</summary>
    internal static IReadOnlyList<string> RequiredBinaries => Binaries;

    /// <summary>
    /// Immutable installed cohort. The maintenance host is staged from the already-signed
    /// running Setup PE rather than downloaded a second time, but it is still integrity-
    /// bound in binaries.manifest and future signed OTA manifests.
    /// </summary>
    internal static IReadOnlyList<string> InstalledCohort =>
        [.. Binaries, MaintenanceContract.ExecutableName];

    /// Maximum download size per binary (200 MB). Aborts if Content-Length exceeds this (H-4).
    private const long MaxDownloadBytes = 200 * 1024 * 1024;
    private const long MaxFieldReleaseReceiptBytes = 64 * 1024;
    internal const int MaxChecksumManifestBytes = 64 * 1024;
    internal const int MaxChecksumSignatureBytes = 512;

    /// Retry schedule for transient HTTP failures (network errors + 5xx).
    /// GitHub release CDN occasionally throws transient 5xx during high traffic;
    /// without retry a single TCP reset would force the user to re-run Setup from scratch.
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(9),
    ];

    /// <summary>
    /// Downloads, verifies, and installs all agent binaries. Returns true on success.
    /// </summary>
    public static async Task<bool> DownloadAndVerifyAsync(string releaseTag, string installDir)
    {
        // A setup session is pinned to one exact signed release. Falling back to "latest"
        // silently changes the intended cohort and can cross a compatibility or approval boundary.
        if (!IsValidReleaseTag(releaseTag))
        {
            ConsoleUI.WriteFail("The installer release identity is invalid; download a fresh installer from Suavo.");
            return false;
        }
        var baseUrl = $"https://github.com/{RepoOwner}/{RepoName}/releases/download/{releaseTag}";

        Directory.CreateDirectory(installDir);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SuavoSetup/1.0");

        // Step 1: download and verify checksums for the exact pinned release only.
        var checksums = await DownloadAndVerifyChecksumsAsync(http, baseUrl, installDir);
        if (checksums == null) return false;

        if (!checksums.TryGetValue(
                MaintenanceContract.FieldReleaseReceiptFileName,
                out var receiptHash))
        {
            ConsoleUI.WriteFail("Signed field release receipt is missing - aborting");
            return false;
        }
        var receiptPath = Path.Combine(
            installDir,
            MaintenanceContract.FieldReleaseReceiptFileName);
        var receiptDownloaded = await RetryTransientAsync(
            () => DownloadFileAsync(
                http,
                $"{baseUrl}/{MaintenanceContract.FieldReleaseReceiptFileName}",
                receiptPath,
                MaintenanceContract.FieldReleaseReceiptFileName,
                MaxFieldReleaseReceiptBytes),
            "download field release receipt");
        if (!receiptDownloaded ||
            new FileInfo(receiptPath).Length is <= 0 or > MaxFieldReleaseReceiptBytes ||
            !HashMatches(receiptPath, receiptHash))
        {
            ConsoleUI.WriteFail("Signed field release receipt did not match this release - aborting");
            Cleanup(receiptPath);
            return false;
        }

        // Step 2: Verify all expected binaries have checksum entries
        foreach (var bin in Binaries)
        {
            if (!checksums.ContainsKey(bin))
            {
                ConsoleUI.WriteFail($"Checksum missing for {bin} - aborting");
                return false;
            }
        }

        // Step 3: Download each runtime binary with progress. The maintenance host
        // is staged from the already-signed running Setup PE after this completes.
        foreach (var bin in Binaries)
        {
            var url = $"{baseUrl}/{bin}";
            var destPath = Path.Combine(installDir, bin);

            ConsoleUI.WriteInfo($"Downloading {bin}...");

            // Retry the whole download on transient failure — the SHA-256 verify
            // below catches any silent corruption, so retrying a stream that
            // broke mid-flight is safe.
            var downloaded = await RetryTransientAsync(
                () => DownloadFileAsync(http, url, destPath, bin),
                $"download {bin}");
            if (!downloaded)
                return false;

            // Verify SHA-256 (QA wave2.5: via the testable HashMatches tamper gate)
            if (!HashMatches(destPath, checksums[bin]))
            {
                ConsoleUI.WriteFail($"SHA-256 mismatch for {bin}");
                ConsoleUI.WriteInfo($"  Expected: {checksums[bin]}");
                ConsoleUI.WriteInfo($"  Actual:   {ComputeSha256(destPath)}");
                CleanupBinaries(installDir);
                return false;
            }

            var sizeMb = new FileInfo(destPath).Length / (1024.0 * 1024.0);
            ConsoleUI.WriteOk($"{bin} verified ({sizeMb:F1} MB)");
        }

        return true;
    }

    internal static bool IsValidReleaseTag(string? releaseTag) =>
        !string.IsNullOrWhiteSpace(releaseTag) &&
        System.Text.RegularExpressions.Regex.IsMatch(
            releaseTag,
            @"^v?[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
            var checksumBytes = await RetryTransientAsync(
                () => DownloadBoundedBytesAsync(
                    http,
                    $"{baseUrl}/checksums.sha256",
                    MaxChecksumManifestBytes,
                    CancellationToken.None),
                "download checksums.sha256");

            // The release signs checksums.sha256 with `openssl dgst -sha256 -sign`,
            // which emits a BINARY, DER (ASN.1)-encoded ECDSA signature — not hex.
            var sigBytes = await RetryTransientAsync(
                () => DownloadBoundedBytesAsync(
                    http,
                    $"{baseUrl}/checksums.sha256.sig",
                    MaxChecksumSignatureBytes,
                    CancellationToken.None),
                "download checksums.sha256.sig");

            var valid = VerifyChecksumSignature(checksumBytes, sigBytes);

            if (!valid)
            {
                ConsoleUI.WriteFail("CRITICAL: Checksum signature verification FAILED - aborting");
                Cleanup(checksumPath, sigPath);
                return null;
            }

            // Only authenticated metadata is persisted into the private stage.
            // Each file lands through a write-through temporary and atomic rename;
            // the outer catch removes both if the second write cannot complete.
            await WriteVerifiedMetadataAsync(checksumPath, checksumBytes);
            await WriteVerifiedMetadataAsync(sigPath, sigBytes);

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
        catch (Exception ex) when (ex is HttpRequestException or
                                   InvalidDataException or
                                   IOException or
                                   UnauthorizedAccessException or
                                   CryptographicException)
        {
            ConsoleUI.WriteFail($"Release metadata download failed. Support code: {DownloadFailureCode(ex)}");
            Cleanup(checksumPath, sigPath);
            return null;
        }
    }

    /// <summary>
    /// Reads small release metadata through a strict header and streaming cap.
    /// The cap is enforced even when the server omits or lies about Content-Length.
    /// </summary>
    internal static async Task<byte[]> DownloadBoundedBytesAsync(
        HttpClient http,
        string url,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));

        using var response = await http.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long declared &&
            (declared <= 0 || declared > maxBytes))
            throw new InvalidDataException("Release metadata declared an invalid size.");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var destination = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
        var buffer = new byte[Math.Min(maxBytes, 8 * 1024)];
        var total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (total > maxBytes - read)
                throw new InvalidDataException("Release metadata exceeded its streaming size limit.");
            destination.Write(buffer, 0, read);
            total += read;
        }
        if (total == 0)
            throw new InvalidDataException("Release metadata was empty.");
        return destination.ToArray();
    }

    private static async Task WriteVerifiedMetadataAsync(string path, byte[] bytes)
    {
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            Cleanup(tempPath);
        }
    }

    /// <summary>
    /// Verifies the ECDSA P-256 / SHA-256 signature over the raw checksums.sha256
    /// bytes. The release signs with `openssl dgst -sign`, so the signature is a
    /// BINARY DER (ASN.1) sequence — not hex, and not the IEEE-P1363 format that
    /// <see cref="ECDsa.VerifyData(byte[], byte[], HashAlgorithmName)"/> assumes.
    /// </summary>
    internal static bool VerifyChecksumSignature(byte[] checksumBytes, byte[] derSignature)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
        return ecdsa.VerifyData(
            checksumBytes, derSignature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    /// <summary>
    /// Downloads a file with progress reporting.
    /// </summary>
    private static async Task<bool> DownloadFileAsync(
        HttpClient http,
        string url,
        string destPath,
        string label,
        long maxBytes = MaxDownloadBytes)
    {
        var completed = false;
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;

            // H-4: Abort if declared size exceeds 200 MB
            if (totalBytes == 0 || totalBytes > maxBytes)
            {
                ConsoleUI.WriteFail($"{label} declared an invalid or oversized payload — aborting");
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
                if (totalRead > maxBytes)
                {
                    ConsoleUI.WriteFail($"{label} exceeded its streaming size limit — aborting");
                    return false;
                }

                if (totalBytes > 0)
                    ConsoleUI.WriteProgress(label, totalRead, totalBytes);
            }

            completed = totalRead > 0;
            return completed;
        }
        catch (HttpRequestException ex) when (IsTransientHttpFailure(ex))
        {
            // Let transient failures (5xx / connection reset) propagate so RetryTransientAsync can
            // retry — swallowing them here (returning false) made the retry wrapper dead code, so a
            // single blip aborted the whole install with zero retries.
            ConsoleUI.WriteInfo($"  {label} was temporarily unavailable. Retrying securely.");
            throw;
        }
        catch (Exception ex)
        {
            ConsoleUI.WriteFail($"Download failed for {label}. Support code: {DownloadFailureCode(ex)}");
            return false;
        }
        finally
        {
            if (!completed) Cleanup(destPath);
        }
    }

    // QA wave2.5 test seam: the exact per-binary tamper gate used by DownloadAndVerifyAsync — the
    // downloaded file's SHA-256 must equal the signed checksum (case-insensitive hex). A regression
    // here (wrong field, case-sensitive compare on uppercase hex) would silently accept a tampered
    // binary on a fresh install.
    internal static bool HashMatches(string filePath, string expectedHashHex) =>
        ComputeSha256(filePath).Equals(expectedHashHex, StringComparison.OrdinalIgnoreCase);

    private static string ComputeSha256(string filePath)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.SequentialScan);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Writes binaries.manifest (name -> sha256 of the on-disk binary) to ProgramData. The Broker's
    /// integrity guard (SessionWatcher.VerifyHelperIntegrity) refuses to launch the Helper if its
    /// on-disk hash != this manifest, then calls Application.Exit -> the agent goes BLIND with no
    /// crash and no app-log error. SelfUpdater.RegenerateBinariesManifest does this after an OTA swap,
    /// but the GUI installer did NOT — so installing/reinstalling over existing binaries left a STALE
    /// manifest and the new Helper was rejected. (Live brick on Mina's box 2026-06-05.) This mirrors
    /// SelfUpdater's manifest shape exactly so OTA and install agree. Call AFTER all binaries are
    /// placed and BEFORE the services start. The manifest is all-or-nothing: a partial cohort can
    /// never be recorded as healthy because the missing executable would strand repair or recovery.
    /// </summary>
    public static bool WriteBinariesManifest(string installDir, string? manifestPathOverride = null)
    {
        var manifestPath = manifestPathOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "binaries.manifest");
        var missing = InstalledCohort
            .Where(bin => !File.Exists(Path.Combine(installDir, bin)))
            .ToArray();
        if (missing.Length > 0)
        {
            ConsoleUI.WriteWarn(
                $"binaries.manifest NOT written — installed cohort is incomplete: {string.Join(", ", missing)}");
            // Never leave a stale prior manifest beside a newly partial cohort. The Broker must
            // fail closed if any external recovery attempt starts it after this installer aborts.
            try { if (File.Exists(manifestPath)) File.Delete(manifestPath); } catch { }
            return false;
        }

        var entries = new List<string>();
        foreach (var bin in InstalledCohort)
        {
            var path = Path.Combine(installDir, bin);
            entries.Add($"  \"{bin}\": \"{ComputeSha256(path)}\"");
        }

        var json = "{\n" + string.Join(",\n", entries) + "\n}\n";
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var tmp = manifestPath + ".tmp";
        File.WriteAllText(tmp, json, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(manifestPath)) File.Replace(tmp, manifestPath, null);
        else File.Move(tmp, manifestPath);
        ConsoleUI.WriteOk($"binaries.manifest written ({entries.Count} binaries) — Broker Helper-integrity root");
        return true;
    }

    /// <summary>
    /// Retries an HTTP operation on transient failures (network errors + 5xx)
    /// with exponential backoff (1s / 3s / 9s). Non-transient failures
    /// (auth, 4xx, file IO) surface immediately on the first attempt.
    /// </summary>
    private static async Task<T> RetryTransientAsync<T>(
        Func<Task<T>> operation, string operationName)
    {
        Exception? lastEx = null;
        for (int attempt = 0; attempt < RetryDelays.Length + 1; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException ex) when (IsTransientHttpFailure(ex))
            {
                lastEx = ex;
                if (attempt < RetryDelays.Length)
                {
                    ConsoleUI.WriteInfo(
                        $"  {operationName} attempt {attempt + 1} was temporarily unavailable; " +
                        $"retrying in {RetryDelays[attempt].TotalSeconds:F0}s");
                    await Task.Delay(RetryDelays[attempt]);
                }
            }
        }

        throw new HttpRequestException(
            $"{operationName} failed after {RetryDelays.Length + 1} attempts.",
            lastEx);
    }

    private static string DownloadFailureCode(Exception exception) => exception switch
    {
        HttpRequestException => "SETUP-DOWNLOAD-NETWORK",
        InvalidDataException => "SETUP-DOWNLOAD-INVALID",
        UnauthorizedAccessException => "SETUP-DOWNLOAD-ACCESS",
        IOException => "SETUP-DOWNLOAD-IO",
        CryptographicException => "SETUP-DOWNLOAD-TRUST",
        _ => "SETUP-DOWNLOAD-FAILED",
    };

    /// <summary>
    /// True for network-level failures (no status code) and HTTP 5xx responses.
    /// 4xx (auth / missing artifact) is non-retryable and surfaces immediately.
    /// </summary>
    private static bool IsTransientHttpFailure(HttpRequestException ex)
    {
        if (ex.StatusCode is null)
            return true;
        var status = (int)ex.StatusCode.Value;
        return status >= 500 && status < 600;
    }

    private static void CleanupBinaries(string installDir)
    {
        foreach (var bin in Binaries)
        {
            var path = Path.Combine(installDir, bin);
            try { File.Delete(path); } catch { /* best effort */ }
        }
        try
        {
            File.Delete(Path.Combine(
                installDir,
                MaintenanceContract.FieldReleaseReceiptFileName));
        }
        catch { /* best effort */ }
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }
}
