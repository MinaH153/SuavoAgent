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
            IScreenExtractor extractor = ScrubbedExtractorFactory.Create(agentOpts, logger);

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

    /// <summary>
    /// Sandbox-only bootstrap for WINDOW-SCOPED PrintWindow capture on a NON-PHI box.
    /// Unlike <see cref="TryBuild"/>, this does NOT require vision.json — sandbox capture is
    /// opt-in per explore_sandbox command, not via an operator config file — but it captures
    /// ONLY the single allowlisted-sandbox window identified by <paramref name="targetHwnd"/>
    /// (PrintWindow is HWND-scoped, so no other window's pixels can leak).
    ///
    /// HIPAA build-time gate: REFUSES construction (returns null) if PioneerRx is installed on
    /// this host. A PMS box must NEVER receive this opt-in-free pipeline — it uses <see cref="TryBuild"/>
    /// (vision.json, default off) instead. Also returns null on non-Windows, a zero HWND, or any error.
    /// </summary>
    public static ScreenCaptureController? TryBuildWindowSandbox(IntPtr targetHwnd, int expectedPid, ILogger logger)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                logger.Information("VisionBootstrap.TryBuildWindowSandbox: disabled (non-Windows)");
                return null;
            }

            // HIPAA build-time gate (defense-in-depth; primary gates are cloud-side command
            // routing + the Core allowlist). Never stand up an opt-in-free capture path on a PMS box.
            if (PioneerRxInstallDetector.IsInstalled(logger))
            {
                logger.Warning("VisionBootstrap.TryBuildWindowSandbox: REFUSED — PioneerRx is installed; " +
                    "window-scoped sandbox capture is non-PHI-box-only");
                return null;
            }

            if (targetHwnd == IntPtr.Zero)
            {
                logger.Warning("VisionBootstrap.TryBuildWindowSandbox: REFUSED — targetHwnd is zero (no resolved sandbox window)");
                return null;
            }

            var opts = new VisionOptions
            {
                Enabled = true,
                Tesseract = new TesseractOptions { Enabled = false }, // UIA-only; OCR off (CPU-cheap, low-spec safe)
            };
            var agentOpts = Options.Create(new AgentOptions { Vision = opts });

            IScreenCapture capture = new WindowScopedScreenCapture(agentOpts, logger, targetHwnd, expectedPid);
            IScreenStore store = new EncryptedScreenStore(agentOpts, logger);
            IScreenExtractor extractor = ScrubbedExtractorFactory.Create(agentOpts, logger);

            logger.Information(
                "VisionBootstrap.TryBuildWindowSandbox: SANDBOX VISION — hwnd=0x{Hwnd:X}, extractor={Ext}",
                targetHwnd.ToInt64(), extractor.ExtractorId);

            return new ScreenCaptureController(capture, store, extractor, logger);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "VisionBootstrap.TryBuildWindowSandbox: failed to build pipeline");
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
