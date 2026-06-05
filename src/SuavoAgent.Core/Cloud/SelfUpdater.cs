using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.Health;

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
        var downloads = new List<(string Url, string Sha256, string Binary)>
        {
            (manifest.CoreUrl, manifest.CoreSha256, "SuavoAgent.Core.exe"),
            (manifest.BrokerUrl, manifest.BrokerSha256, "SuavoAgent.Broker.exe"),
            (manifest.HelperUrl, manifest.HelperSha256, "SuavoAgent.Helper.exe"),
        };
        // Self-heal Chunk B: the Watchdog binary ships only when the manifest carries it (back-compat).
        if (manifest.HasWatchdog)
            downloads.Add((manifest.WatchdogUrl!, manifest.WatchdogSha256!, "SuavoAgent.Watchdog.exe"));

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
        CleanupOldBinaries(installDir, logger);
    }

    /// <summary>
    /// Reap stale <c>.exe.old</c> rollback binaries — UNLESS an OTA is on probation, in which case
    /// the <c>.old</c> set is the rollback target and must be preserved until the new set commits as
    /// healthy (Codex 2026-06-04 P1: this cleanup runs at HeartbeatWorker startup BEFORE any health
    /// confirmation, and would otherwise make downgrade impossible).
    /// </summary>
    public static void CleanupOldBinaries(string installDir, ILogger logger)
    {
        if (string.IsNullOrEmpty(installDir)) return;

        if (new OtaProbationStore(installDir).HasProbation)
        {
            logger.LogDebug("Skipping .old cleanup — an OTA is on probation; rollback set preserved");
            return;
        }

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

    /// <summary>
    /// Crash-loop downgrade: restore the last-good <c>.old</c> set over the current binaries,
    /// ALL-OR-NOTHING with its own rollback (mirroring <see cref="SwapBinaries"/>) so a partial
    /// restore can never leave Core/Broker/Helper version skew (Codex 2026-06-04 P1). Returns false
    /// (changing nothing) when the full <c>.old</c> set isn't present. The caller (early bootstrap)
    /// then queues a Watchdog restart so the restored set is actually loaded — Broker/Helper .exe are
    /// locked while running, so this only swaps files on disk.
    /// </summary>
    public static bool RestoreOldBinaries(string installDir, ILogger logger)
    {
        var binaries = new[] { "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe" };

        var missing = binaries.Where(b => !File.Exists(Path.Combine(installDir, b + ".old"))).ToList();
        if (missing.Count > 0)
        {
            logger.LogWarning("Cannot downgrade — missing rollback set {Missing}; staying on current binaries",
                string.Join(", ", missing));
            return false;
        }

        // Each entry: (current path, the displaced-bad backup path) so we can undo a partial restore.
        var done = new List<(string Current, string Bad)>();
        try
        {
            foreach (var bin in binaries)
            {
                var current = Path.Combine(installDir, bin);
                var oldFile = current + ".old";
                var bad = current + ".bad";

                if (File.Exists(bad)) File.Delete(bad);
                if (File.Exists(current)) File.Move(current, bad);
                File.Move(oldFile, current);
                done.Add((current, bad));
                logger.LogInformation("Downgraded {Binary} to last-good", bin);
            }

            // Success — discard the bad set.
            foreach (var (_, bad) in done)
                try { if (File.Exists(bad)) File.Delete(bad); } catch { /* best-effort */ }
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Downgrade restore failed after {Count} — rolling back the restore", done.Count);
            foreach (var (current, bad) in done)
            {
                try
                {
                    // current now holds the restored .old → put it back as .old; bad → current.
                    if (File.Exists(current)) File.Move(current, current + ".old");
                    if (File.Exists(bad)) File.Move(bad, current);
                }
                catch { /* best-effort rollback */ }
            }
            return false;
        }
    }

    /// <summary>What the bootstrap should do about an OTA on probation, decided early (before risky init).</summary>
    public enum ProbationStartupAction
    {
        /// <summary>No OTA on trial — normal startup.</summary>
        NoProbation,
        /// <summary>On trial, under the crash-loop budget — proceed with startup.</summary>
        Continue,
        /// <summary>Crash-loop: the <c>.old</c> set was restored — Exit so the Watchdog reloads it.</summary>
        RestartForDowngrade,
        /// <summary>Crash-loop but no rollback set — can't self-heal; log CRITICAL + proceed (operator needed).</summary>
        EscalateNoRollback,
    }

    private static readonly string[] OtaBinaries = { "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe" };

    private static bool AllOldPresent(string installDir) =>
        OtaBinaries.All(b => File.Exists(Path.Combine(installDir, b + ".old")));

    /// <summary>
    /// Early-bootstrap crash-loop guard: if an OTA is on probation, increment the boot counter (atomic,
    /// fail-closed) and let <see cref="OtaProbationEvaluator"/> decide. On Downgrade, restore the
    /// last-good set all-or-nothing and signal a restart; if the restore can't proceed, escalate. The
    /// caller runs this BEFORE risky init so a crash-loop converges to a downgrade.
    /// </summary>
    public static ProbationStartupAction HandleProbationOnStartup(string installDir, int maxProbationBoots, ILogger logger)
    {
        var store = new OtaProbationStore(installDir);
        if (!store.HasProbation) return ProbationStartupAction.NoProbation;

        int boots;
        try { boots = store.IncrementBoots(); }
        catch (Exception ex)
        {
            logger.LogError(ex, "OTA probation boot-count write failed — escalating (cannot guarantee crash-loop convergence)");
            return ProbationStartupAction.EscalateNoRollback;
        }

        var state = new OtaProbationState(
            HasPendingProbation: true,
            HealthConfirmed: false, // health is confirmed LATE, after init; early we only check crash-loop
            BootsSinceSwap: boots,
            MaxProbationBoots: maxProbationBoots,
            OldSetAvailable: AllOldPresent(installDir));

        switch (OtaProbationEvaluator.Evaluate(state))
        {
            case OtaProbationDecision.Downgrade:
                if (RestoreOldBinaries(installDir, logger))
                {
                    store.Clear();
                    logger.LogWarning("OTA crash-loop ({Boots} boots) — downgraded to last-good; restarting", boots);
                    return ProbationStartupAction.RestartForDowngrade;
                }
                logger.LogCritical("OTA crash-loop but downgrade restore FAILED — operator intervention needed");
                return ProbationStartupAction.EscalateNoRollback;

            case OtaProbationDecision.EscalateNoRollback:
                logger.LogCritical("OTA crash-loop ({Boots} boots) with NO rollback set — operator intervention needed", boots);
                return ProbationStartupAction.EscalateNoRollback;

            default:
                return ProbationStartupAction.Continue;
        }
    }

    /// <summary>
    /// Late health milestone (first good heartbeat + IPC up): commit the OTA — clear probation and reap
    /// the <c>.old</c> rollback set. No-op when nothing is on trial.
    /// </summary>
    public static void ConfirmHealthyAndReap(string installDir, ILogger logger)
    {
        var store = new OtaProbationStore(installDir);
        if (!store.HasProbation) return;
        store.Clear();
        CleanupOldBinaries(installDir, logger); // flag cleared ⇒ now permitted to reap
        logger.LogInformation("OTA confirmed healthy — committed; rollback set reaped");
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

        // #10: require the FULL staged set. The OTA staging path always downloads Core+Broker+Helper
        // together (versioned + signed as a unit), so a partial set at swap time means a corrupted or
        // partial stage. Swapping only the present subset would leave version skew — a new Core running
        // against an old Broker/Helper — the silent-corruption class this guard exists to prevent.
        // Refuse and swap nothing; the caller discards the staged .new files and the box stays on the
        // last-good set.
        var missing = new List<string>();
        foreach (var bin in binaries)
            if (!File.Exists(Path.Combine(installDir, bin + ".new"))) missing.Add(bin);
        if (missing.Count > 0)
        {
            // Distinguish "nothing staged" (normal — no pending swap) from a genuine partial set.
            if (missing.Count < binaries.Length)
                logger.LogWarning(
                    "Refusing partial binary swap — missing staged {Missing}; keeping last-good set to avoid version skew",
                    string.Join(", ", missing));
            return false;
        }

        var swapped = new List<string>();

        try
        {
            foreach (var bin in binaries)
            {
                var current = Path.Combine(installDir, bin);
                var newFile = current + ".new";
                var oldFile = current + ".old";

                // All .new files were verified present above — swap unconditionally.
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

    /// <summary>
    /// Best-effort swap of the staged Watchdog binary. Unlike <see cref="SwapBinaries"/> this never
    /// fails the overall update: if the Watchdog service holds a lock on its .exe (running) the swap
    /// is skipped and the .new is left for a later restart. Returns true iff a swap occurred.
    /// </summary>
    internal static bool SwapWatchdogBestEffort(string installDir, ILogger logger)
    {
        const string bin = "SuavoAgent.Watchdog.exe";
        var current = Path.Combine(installDir, bin);
        var newFile = current + ".new";
        var oldFile = current + ".old";

        if (!File.Exists(newFile)) return false;
        try
        {
            if (File.Exists(oldFile)) File.Delete(oldFile);
            if (File.Exists(current)) File.Move(current, oldFile);
            File.Move(newFile, current);
            logger.LogInformation("Swapped {Binary} (best-effort)", bin);
            return true;
        }
        catch (Exception ex)
        {
            // Locked (service running) or other IO — roll back this one binary, keep .new for later.
            logger.LogWarning(ex, "Watchdog swap skipped (best-effort) — likely locked; leaving staged binary");
            try { if (!File.Exists(current) && File.Exists(oldFile)) File.Move(oldFile, current); } catch { }
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
                // OTA crash-loop guard: the just-swapped set is now ON PROBATION. The next startup
                // increments the boot counter; if it crash-loops it auto-downgrades to the .old set,
                // and a healthy first-heartbeat commits + reaps. The .old set is preserved meanwhile
                // (CleanupOldBinaries is probation-guarded).
                try { new OtaProbationStore(installDir).Begin(manifest.Version, DateTimeOffset.UtcNow); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not start OTA probation — proceeding without crash-loop guard"); }

                // Self-heal Chunk B: swap the Watchdog separately + best-effort. If the Watchdog
                // service is running its .exe is locked — skip without failing the (already-applied)
                // Core/Broker/Helper swap; the .new stays for a later attempt. For a box MISSING the
                // Watchdog there's no lock, so this drops the binary in for the Broker to register.
                SwapWatchdogBestEffort(installDir, logger);

                // CRITICAL: regenerate binaries.manifest from the just-swapped binaries. The
                // Broker's H-8 guard (SessionWatcher.VerifyHelperIntegrity) refuses to launch the
                // Helper if its on-disk hash != the manifest. Before this, an OTA/bootstrap swap
                // updated the .exes but left the OLD manifest, so the new Helper was rejected and the
                // agent went BLIND (vision/UIA offline) with no crash and no app-log error — exactly
                // the field failure on Mina's box after the v3.17.0 swap. Regenerate AFTER the swap so
                // the manifest reflects what's actually on disk.
                var manifestOk = RegenerateBinariesManifest(installDir, logger);
                if (!manifestOk)
                {
                    // The swap already applied, so we can't un-apply — but a stale/failed
                    // manifest means the Broker's H-8 guard will REFUSE to launch the Helper
                    // (the silent-blind failure this whole fix exists to prevent). Make it
                    // LOUD instead of reporting clean success: record update health 'degraded'
                    // so the cloud agent-health-watch can alarm, and log an error. Best-effort
                    // on the health write itself — never throw out of the bootstrap path.
                    try
                    {
                        RuntimeHealthEvidence.WriteUpdateHealth(
                            RuntimeHealthEvidence.UpdateHealthPath(),
                            status: "degraded",
                            targetVersion: manifest.Version,
                            lastAttemptAt: DateTimeOffset.UtcNow,
                            lastSuccessAt: null,
                            consecutiveFailures: 1,
                            lastErrorKind: "manifest_regen_failed",
                            channel: null);
                    }
                    catch (Exception hx)
                    {
                        logger.LogError(hx, "Failed to record degraded update health after manifest regen failure");
                    }
                    logger.LogError(
                        "Bootstrap update v{Version} applied but binaries.manifest regen FAILED — Helper will be refused until the manifest is refreshed (update marked degraded)",
                        manifest.Version);
                }

                File.Delete(sentinel);

                // Signal the LocalSystem Watchdog to cycle the Broker onto the new binary. SCM only
                // restarts Core on our Exit below; without this the Broker keeps running the OLD
                // binary, #130 never re-runs, the orphan Helper holds the IPC pipe, and the box
                // strands at ipc_unreachable until a manual restart (the field failure this fixes).
                WatchdogRestartRequestWriter.Queue(installDir, manifest.Version, logger);

                logger.LogInformation("Bootstrap update applied — v{Version}{Suffix}",
                    manifest.Version, manifestOk ? "" : " (DEGRADED: manifest regen failed)");
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

    internal static bool VerifyStagedBinaries(string installDir, UpdateManifest manifest, ILogger logger)
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SuavoAgent.Core.exe"]   = manifest.CoreSha256,
            ["SuavoAgent.Broker.exe"] = manifest.BrokerSha256,
            ["SuavoAgent.Helper.exe"] = manifest.HelperSha256,
        };
        // Self-heal Chunk B: a staged Watchdog binary must match the (signed) manifest hash too.
        if (manifest.HasWatchdog)
            expected["SuavoAgent.Watchdog.exe"] = manifest.WatchdogSha256!;

        foreach (var (binary, expectedHash) in expected)
        {
            var newPath = Path.Combine(installDir, binary + ".new");
            if (!File.Exists(newPath))
            {
                // #10: a declared binary with no staged .new is a partial/corrupted stage. Abort the
                // whole update (the caller discards the staged files) rather than letting SwapBinaries
                // apply a subset and leave version skew.
                logger.LogWarning("Staged binary missing for {Binary} — partial update, aborting swap", binary);
                return false;
            }

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

    /// <summary>
    /// Rewrites %ProgramData%\SuavoAgent\binaries.manifest from the binaries CURRENTLY on disk in
    /// <paramref name="installDir"/>, so the Broker's H-8 integrity guard
    /// (SessionWatcher.VerifyHelperIntegrity) validates the post-swap Helper instead of refusing to
    /// launch it against a stale hash. Format + path mirror install.ps1: a JSON object of
    /// { "SuavoAgent.&lt;X&gt;.exe": "&lt;lowercase-hex-sha256&gt;" } at CommonApplicationData\SuavoAgent.
    /// Best-effort: a swap already succeeded, so never throw — a stale manifest only costs a Helper
    /// relaunch, and a missing manifest makes the guard fail-OPEN (skips the check). Written
    /// atomically (temp + replace) so the Broker can never read a half-written manifest mid-poll.
    /// The manifestPathOverride param exists only so tests can target a temp dir.
    /// </summary>
    /// <returns>true if the manifest was rewritten to match on-disk binaries; false on
    /// any failure (no binaries found, or an IO/exception during write) — the caller MUST
    /// treat false as a degraded update, never as clean success.</returns>
    internal static bool RegenerateBinariesManifest(
        string installDir, ILogger logger, string? manifestPathOverride = null)
    {
        try
        {
            var binaries = new[]
            {
                "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe",
                "SuavoAgent.Helper.exe", "SuavoAgent.Watchdog.exe",
            };

            var entries = new List<string>();
            foreach (var bin in binaries)
            {
                var path = Path.Combine(installDir, bin);
                if (!File.Exists(path)) continue; // e.g. a box without the Watchdog binary
                using var stream = File.OpenRead(path);
                var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                // Manual JSON to match install.ps1's ConvertTo-Json shape with no extra deps.
                entries.Add($"  \"{bin}\": \"{hash}\"");
            }

            if (entries.Count == 0)
            {
                logger.LogError("RegenerateBinariesManifest: no binaries found in {Dir} — manifest NOT refreshed (degraded)", installDir);
                return false;
            }

            var json = "{\n" + string.Join(",\n", entries) + "\n}\n";

            var manifestPath = manifestPathOverride ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "binaries.manifest");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            var tempPath = manifestPath + ".tmp";

            File.WriteAllText(tempPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            if (File.Exists(manifestPath))
                File.Replace(tempPath, manifestPath, null);
            else
                File.Move(tempPath, manifestPath);

            logger.LogInformation("Regenerated binaries.manifest ({Count} binaries) after swap", entries.Count);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RegenerateBinariesManifest failed — Helper may be refused until manifest is refreshed");
            return false;
        }
    }
}
