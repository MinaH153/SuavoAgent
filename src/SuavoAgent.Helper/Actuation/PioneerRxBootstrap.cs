using System.Text.Json;
using Serilog;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Loads <see cref="PioneerRxConfig"/> from
/// <c>%PROGRAMDATA%\SuavoAgent\pioneerrx.json</c>. Mirrors
/// <see cref="ActuationBootstrap"/>'s pattern.
/// </summary>
public static class PioneerRxBootstrap
{
    public const string ConfigFileName = "pioneerrx.json";

    private sealed record ConfigJson(bool? Enabled, bool? DryRun, string? ProcessName);

    public static PioneerRxConfig LoadConfig(ILogger logger)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            ConfigFileName);

        if (!File.Exists(path))
        {
            logger.Information("PioneerRx: no config at {Path}, using safe default (disabled)", path);
            return PioneerRxConfig.SafeDefault();
        }

        try
        {
            var raw = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<ConfigJson>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (parsed is null)
            {
                logger.Warning("PioneerRx: empty/null config at {Path}, using safe default", path);
                return PioneerRxConfig.SafeDefault();
            }
            var safe = PioneerRxConfig.SafeDefault();
            return safe with
            {
                Enabled = parsed.Enabled ?? safe.Enabled,
                DryRun = parsed.DryRun ?? safe.DryRun,
                ProcessName = string.IsNullOrWhiteSpace(parsed.ProcessName) ? safe.ProcessName : parsed.ProcessName,
            };
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "PioneerRx: failed to read {Path}, using safe default", path);
            return PioneerRxConfig.SafeDefault();
        }
    }
}
