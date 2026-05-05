using System.Text.Json;
using Serilog;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Loads <see cref="ActuationConfig"/> from
/// <c>%PROGRAMDATA%\SuavoAgent\actuation.json</c> if present, otherwise
/// returns the safe default (Enabled=false, DryRun=true). The JSON file
/// is the per-host knob operators flip after the cloud-side consent
/// + first-ever owner-MFA approval flow.
///
/// Schema:
/// {
///   "Enabled": false,
///   "DryRun": true,
///   "UserInputPauseSeconds": 300,
///   "DefaultUiaTimeoutMs": 8000,
///   "DefaultPerKeyDelayMs": 25,
///   "DefaultInterChordDelayMs": 80
/// }
/// </summary>
public static class ActuationBootstrap
{
    public const string ConfigFileName = "actuation.json";

    private sealed record ConfigJson(
        bool? Enabled,
        bool? DryRun,
        int? UserInputPauseSeconds,
        int? DefaultUiaTimeoutMs,
        int? DefaultPerKeyDelayMs,
        int? DefaultInterChordDelayMs);

    public static ActuationConfig LoadConfig(ILogger logger)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            ConfigFileName);

        if (!File.Exists(path))
        {
            logger.Information("Actuation: no config at {Path}, using safe default (disabled, dry-run)", path);
            return ActuationConfig.SafeDefault();
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
                logger.Warning("Actuation: empty/null config at {Path}, using safe default", path);
                return ActuationConfig.SafeDefault();
            }

            var safe = ActuationConfig.SafeDefault();
            return safe with
            {
                Enabled = parsed.Enabled ?? safe.Enabled,
                DryRun = parsed.DryRun ?? safe.DryRun,
                UserInputPauseWindow = parsed.UserInputPauseSeconds is { } secs
                    ? TimeSpan.FromSeconds(Math.Clamp(secs, 60, 3600))
                    : safe.UserInputPauseWindow,
                DefaultUiaTimeout = parsed.DefaultUiaTimeoutMs is { } ms
                    ? TimeSpan.FromMilliseconds(Math.Clamp(ms, 500, 60_000))
                    : safe.DefaultUiaTimeout,
                DefaultPerKeyDelayMs = parsed.DefaultPerKeyDelayMs is { } pkd
                    ? Math.Clamp(pkd, 0, 500)
                    : safe.DefaultPerKeyDelayMs,
                DefaultInterChordDelayMs = parsed.DefaultInterChordDelayMs is { } icd
                    ? Math.Clamp(icd, 0, 1000)
                    : safe.DefaultInterChordDelayMs,
            };
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Actuation: failed to read {Path}, using safe default", path);
            return ActuationConfig.SafeDefault();
        }
    }
}
