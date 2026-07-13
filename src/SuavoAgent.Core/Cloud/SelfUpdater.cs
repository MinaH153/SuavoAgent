using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Maintenance;

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
    internal const string CommandPublicKeyDer = RemoteCommandTrust.CommandV1PublicKeyDer;

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
    public static Task<bool> TryApplyUpdateAsync(
        string downloadUrl, string expectedSha256, string version, string? signature,
        ILogger logger, CancellationToken ct)
    {
        logger.LogError(
            "Legacy single-binary self-activation is disabled; only signed SYSTEM-coordinated cohort updates are accepted");
        return Task.FromResult(false);
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
        => VerifyManifestSignature(manifestCanonical, signatureHex, UpdatePublicKeyDer, logger);

    // Key-injectable overload (QA wave2.5 test seam): the production path above passes the embedded
    // UpdatePublicKeyDer, so behavior is unchanged. Tests pass a generated key so the full
    // ImportSubjectPublicKeyInfo + P1363 VerifyData round-trip is exercised on the ACCEPTANCE path —
    // the old test could only assert a NON-matching key fails. A key-rotation / encoding regression
    // that made the OTA verify accept nothing (brick all agents) would now be caught.
    internal static bool VerifyManifestSignature(
        string manifestCanonical, string? signatureHex, string publicKeyDerBase64, ILogger logger)
    {
        if (string.IsNullOrEmpty(signatureHex))
        {
            logger.LogWarning("Update manifest has no signature — rejecting");
            return false;
        }

        // ECDSA P-256 P1363 is exactly r(32) || s(32), hex-encoded without decoration.
        // Being explicit prevents a DER signature or whitespace-normalized value from being
        // accepted here but rejected later by the maintenance trust verifier.
        if (signatureHex.Length != 128 || !signatureHex.All(Uri.IsHexDigit))
        {
            logger.LogWarning("Update manifest signature is not exact P1363 hex — rejecting");
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyDerBase64), out _);

            var manifestBytes = Encoding.UTF8.GetBytes(manifestCanonical);
            var signatureBytes = Convert.FromHexString(signatureHex);

            var valid = ecdsa.VerifyData(
                manifestBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

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
            logger.LogSafeWarning(ex);
            return false;
        }
    }

    private static bool ContainsControlChars(string s) =>
        s.AsSpan().IndexOfAny('\n', '\r', '|') >= 0;

    /// <summary>
    /// Downloads a signed 11/13-field cohort only into the fixed ProgramData incoming area, verifies
    /// every hash, then atomically publishes the original signed-command envelope as an activation
    /// request. Core is LocalService and never mutates or restarts the installed cohort; Watchdog and
    /// the native SYSTEM maintenance coordinator own every activation decision.
    /// </summary>
    public static async Task<bool> TryApplyPackageUpdateAsync(
        UpdateManifest manifest,
        string signatureHex,
        SignedCommand signedCommand,
        string dataJson,
        ILogger logger,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        return await TryStagePackageUpdateAsync(
            manifest,
            signatureHex,
            signedCommand,
            dataJson,
            UpdateActivationContract.DefaultUpdateRoot(),
            (url, path, token) => DownloadWithSizeLimitAsync(http, url, path, token),
            logger,
            ct);
    }

    internal static async Task<bool> TryStagePackageUpdateAsync(
        UpdateManifest manifest,
        string signatureHex,
        SignedCommand signedCommand,
        string dataJson,
        string updateRoot,
        Func<string, string, CancellationToken, Task> download,
        ILogger logger,
        CancellationToken ct,
        DateTimeOffset? nowOverride = null,
        IReadOnlyDictionary<string, string>? trustedCommandKeys = null,
        string? updatePublicKeyDerBase64 = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(updateRoot);
        ArgumentNullException.ThrowIfNull(download);

        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var stagingId = UpdateActivationContract.ComputeStagingId(
            signedCommand.Nonce,
            signedCommand.DataHash);
        var request = new UpdateActivationRequest(
            UpdateActivationContract.SchemaVersion,
            signedCommand.Command,
            signedCommand.AgentId,
            signedCommand.MachineFingerprint,
            signedCommand.Timestamp,
            signedCommand.Nonce,
            signedCommand.KeyId,
            signedCommand.Signature,
            dataJson,
            signedCommand.DataHash,
            manifest.ToCanonical(),
            signatureHex,
            stagingId,
            now.ToString("O"));
        var validation = UpdateActivationContract.Validate(
            request,
            trustedCommandKeys ?? RemoteCommandTrust.CreateProductionKeyRegistry(),
            updatePublicKeyDerBase64 ?? UpdateActivationContract.ProductionUpdatePublicKeyDer,
            now,
            signedCommand.AgentId,
            signedCommand.MachineFingerprint);
        if (!validation.IsValid)
        {
            logger.LogWarning("core.update.activation_request_rejected");
            return false;
        }

        var downloads = new List<(string Url, string Sha256, string Binary)>
        {
            (manifest.CoreUrl, manifest.CoreSha256, "SuavoAgent.Core.exe"),
            (manifest.BrokerUrl, manifest.BrokerSha256, "SuavoAgent.Broker.exe"),
            (manifest.HelperUrl, manifest.HelperSha256, "SuavoAgent.Helper.exe"),
        };
        if (manifest.HasWatchdog)
            downloads.Add((manifest.WatchdogUrl!, manifest.WatchdogSha256!, "SuavoAgent.Watchdog.exe"));
        if (manifest.HasMaintenance)
            downloads.Add((
                manifest.MaintenanceUrl!,
                manifest.MaintenanceSha256!,
                MaintenanceContract.ExecutableName));

        foreach (var d in downloads)
        {
            if (!IsAllowedUrl(d.Url))
            {
                logger.LogWarning("core.update.url_rejected");
                return false;
            }
        }

        var requestPath = Path.Combine(updateRoot, UpdateActivationContract.ActivationRequestFileName);
        var requestTemp = requestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var stagingDir = UpdateActivationContract.GetIncomingStagingDirectory(updateRoot, stagingId);
        try
        {
            Directory.CreateDirectory(updateRoot);
            Directory.CreateDirectory(Path.Combine(updateRoot, UpdateActivationContract.IncomingDirectoryName));
            if (File.Exists(requestPath))
            {
                logger.LogWarning("An activation request is already pending; refusing to overwrite it");
                return false;
            }
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);

            foreach (var d in downloads)
            {
                var finalPath = Path.Combine(stagingDir, d.Binary);
                var partialPath = finalPath + ".partial";

                logger.LogInformation("core.update.download_started");
                await download(d.Url, partialPath, ct);

                var actualHash = await ComputeSha256Async(partialPath, ct);
                if (!string.Equals(actualHash, d.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("core.update.hash_mismatch");
                    Directory.Delete(stagingDir, recursive: true);
                    return false;
                }
                File.Move(partialPath, finalPath);
                logger.LogInformation("core.update.binary_verified");
            }

            // Refresh the unsigned local handoff timestamp only after all bytes are durable. The
            // signed command timestamp remains the authoritative freshness boundary.
            request = request with
            {
                RequestedAtUtc = (nowOverride ?? DateTimeOffset.UtcNow).ToString("O"),
            };
            await File.WriteAllTextAsync(
                requestTemp,
                UpdateActivationContract.Serialize(request),
                new UTF8Encoding(false),
                ct);
            File.Move(requestTemp, requestPath, overwrite: false);

            logger.LogInformation(
                "core.update.cohort_staged count={Count}",
                downloads.Count);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogSafeWarning(ex);
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); } catch { }
            return false;
        }
        finally
        {
            try { if (File.Exists(requestTemp)) File.Delete(requestTemp); } catch { }
        }
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

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
