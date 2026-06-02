using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Writes the post-OTA restart-request that the LocalSystem Watchdog consumes to cycle the
/// Broker onto the freshly-swapped binary.
///
/// Why this exists: after CheckPendingUpdate swaps the binaries on disk, Core Environment.Exit()s
/// and Windows SCM restarts ONLY Core (Core runs as least-privilege LocalService). The Broker keeps
/// running its OLD in-memory binary and still reports RUNNING, so the Watchdog treats it as healthy
/// and SessionWatcher.ReconcileOrphanHelpers() (#130) never re-runs — the orphan Helper keeps the
/// Core↔Helper IPC pipe and the box strands at ipc_unreachable. The Watchdog (LocalSystem) is the
/// only component that can cycle the Broker, so Core signals it here.
///
/// Security: the file lives in the INSTALL dir (alongside update-pending.flag), whose ACL grants
/// interactive users only ReadAndExecute — so a logged-in pharmacy user can't forge it. The
/// Watchdog also strictly validates schema, exact service allowlist, and TTL freshness.
/// </summary>
internal static class WatchdogRestartRequestWriter
{
    public const string FileName = "watchdog-restart-request.json";

    public static void Queue(string installDir, string version, ILogger logger)
    {
        try
        {
            var path = Path.Combine(installDir, FileName);
            var payload = new
            {
                schemaVersion = 1,
                version,
                requestedAt = DateTimeOffset.UtcNow.ToString("o"),
                services = new[] { "SuavoAgent.Broker" },
            };

            var json = JsonSerializer.Serialize(payload);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);

            logger.LogInformation("Queued watchdog restart-request to cycle Broker onto v{Version} (post-OTA)", version);
        }
        catch (Exception ex)
        {
            // Best-effort: the swap already succeeded. If this write fails the Broker may need a
            // manual restart to load the new binary, so make it LOUD rather than silently strand.
            logger.LogError(ex,
                "Failed to queue watchdog restart-request — Broker may need a manual restart to load v{Version}",
                version);
        }
    }
}
