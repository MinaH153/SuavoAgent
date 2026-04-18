using System.Text.Json;
using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Builds the vision pipeline from an operator-placed config file. Mirrors
/// the Tier-2 pattern: vision is OFF by default, and enabling it is a
/// conscious two-part opt-in (config file + acknowledgement of the HIPAA
/// surface it adds).
///
/// Config file location: %ProgramData%\SuavoAgent\vision.json
/// Contents: JSON-serialized VisionOptions. Missing or unreadable = disabled.
///
/// Returns null (no vision) on:
///   - non-Windows platform
///   - config missing / unparseable
///   - Enabled=false
///   - any construction error
/// </summary>
public static class VisionBootstrap
{
    public static ScreenCaptureController? TryBuild(ILogger logger)
    {
        try
        {
            var opts = LoadOptions(logger);
            if (!opts.Enabled)
            {
                logger.Information("Vision disabled (Enabled=false). To enable, drop vision.json at %ProgramData%\\SuavoAgent\\");
                return null;
            }

            if (!OperatingSystem.IsWindows())
            {
                logger.Information("Vision disabled (non-Windows platform)");
                return null;
            }

            // Wrap options into an IOptions<AgentOptions> for the pipeline
            // services that expect that shape.
            var agentOpts = Options.Create(new AgentOptions { Vision = opts });

            IScreenCapture capture = OperatingSystem.IsWindows()
                ? new GdiScreenCapture(agentOpts, logger)
                : new NullScreenCapture();
            // EncryptedScreenStore is Windows-only (C-3 — no plaintext fallback).
            // Constructor throws on non-Windows hosts, ACL failures, bad paths.
            IScreenStore store = new EncryptedScreenStore(agentOpts, logger);
            IScreenExtractor extractor = ScrubbedExtractorFactory.CreateDefault();

            logger.Information(
                "Vision ENABLED — capture={CaptureAvailable}, retention={RetHours}h, cap={Max}, extractor={Ext}",
                capture.IsAvailable, opts.RetentionHours, opts.MaxStoredScreens, extractor.ExtractorId);

            return new ScreenCaptureController(capture, store, extractor, logger);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "VisionBootstrap: failed to build pipeline — continuing without vision");
            return null;
        }
    }

    private static VisionOptions LoadOptions(ILogger logger)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "vision.json");

        if (!File.Exists(path)) return new VisionOptions();

        try
        {
            var json = File.ReadAllText(path);
            var opts = JsonSerializer.Deserialize<VisionOptions>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return opts ?? new VisionOptions();
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "VisionBootstrap: failed to parse {Path} — disabling vision", path);
            return new VisionOptions();
        }
    }
}
