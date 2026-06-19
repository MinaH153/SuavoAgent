using System;
using System.IO;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>Loads presence.json from %PROGRAMDATA%\SuavoAgent. Mirrors ActuationBootstrap.</summary>
public static class PresenceBootstrap
{
    public const string ConfigFileName = "presence.json";

    public static PresencePreferences LoadConfig(ILogger logger)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            ConfigFileName);

        if (!File.Exists(path))
        {
            logger.Information("Presence: no config at {Path}, using safe default (visible)", path);
            return PresencePreferences.SafeDefault();
        }

        try
        {
            return PresencePreferences.FromJson(File.ReadAllText(path), logger);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Presence: failed to read {Path}, using safe default", path);
            return PresencePreferences.SafeDefault();
        }
    }
}
