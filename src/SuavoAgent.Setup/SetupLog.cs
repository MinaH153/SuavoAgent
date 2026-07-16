using SuavoAgent.Diagnostics.Phi;

namespace SuavoAgent.Setup;

/// <summary>
/// Best-effort file mirror of every install-step line. The GUI installer has no
/// console, so without this a failed install leaves ZERO on-box evidence (the
/// 2026-06-10 fresh-install brick: Watchdog.exe missing → no services registered,
/// GUI said "Installation complete", logs dir empty). Never throws — logging must
/// not be able to break an install.
/// </summary>
internal static class SetupLog
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent", "logs", "setup.log");

    public static void Append(string level, string message)
    {
        try
        {
            var safeMessage = SanitizeForLog(message);
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {safeMessage}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort by contract.
        }
    }

    internal static string SanitizeForLog(string? message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(message)) return "[empty setup event]";
            var bounded = message.Length > 4096 ? message[..4096] : message;

            // Setup messages are an untrusted display/log boundary, not a data
            // transformation surface. If any PHI marker exists, drop the whole
            // event. Partial scrubbing can leave an unlabelled trailing name
            // (for example "Rx # 123 for Jane Doe"), which is unacceptable.
            if (PhiTextScrubber.ContainsPhi(bounded))
                return "[redacted setup event]";

            var scrubbed = PhiTextScrubber.ScrubText(bounded);
            if (string.IsNullOrEmpty(scrubbed)
                || string.Equals(scrubbed, PhiTextScrubber.ScrubTimeoutSentinel, StringComparison.Ordinal)
                || PhiTextScrubber.ContainsPhi(scrubbed))
            {
                return "[redacted setup event]";
            }

            // Keep one physical event per line so attacker-controlled exception
            // text cannot forge log levels or timestamps.
            return scrubbed.Replace('\r', ' ').Replace('\n', ' ');
        }
        catch
        {
            // Privacy is fail-closed even if a future scrubber rule regresses.
            return "[redacted setup event]";
        }
    }
}
