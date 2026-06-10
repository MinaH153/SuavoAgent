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
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort by contract.
        }
    }
}
