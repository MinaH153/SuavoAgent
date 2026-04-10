using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Downloads a new agent binary, verifies SHA256, swaps in-place, exits.
/// Windows service auto-restart policy brings the new binary online.
/// Called once per pending update — never re-attempts on failure.
/// </summary>
public static class SelfUpdater
{
    public static async Task<bool> TryApplyUpdateAsync(
        string downloadUrl, string expectedSha256, string version,
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
                logger.LogWarning("SHA256 mismatch: expected {Expected}, got {Actual} — aborting update",
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

            // 4. Exit — service manager restarts us with the new binary
            logger.LogInformation("Exiting for restart with new binary...");
            Environment.Exit(0);
            return true; // unreachable but keeps compiler happy
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Self-update failed — continuing with current binary");
            try { if (File.Exists(newExe)) File.Delete(newExe); } catch { }
            return false;
        }
    }

    /// <summary>
    /// Cleans up old binary from a previous update on startup.
    /// </summary>
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

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
