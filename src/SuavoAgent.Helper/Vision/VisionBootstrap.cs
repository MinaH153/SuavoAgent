using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Vision;
using SuavoAgent.Helper.Workflows;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Builds both vision consumers from the same strict machine-registry state.
/// A missing state is explicit default-disabled. Invalid state is a startup
/// error and is never converted into a silent disabled fallback.
///
/// Returns null (no vision) on:
///   - non-Windows platform
///   - Enabled=false
///   - any construction error
/// </summary>
public sealed record VisionBootstrapResult(
    ScreenCaptureController? CaptureController,
    VisionPricingGridReader? PricingReader,
    VisionRuntimeStatusTracker RuntimeStatus);

public static class VisionBootstrap
{
    public static VisionConfigurationLoadResult LoadConfiguration(
        ILogger logger,
        IVisionConfigurationStore? store = null,
        string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var root = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent");
        var loaded = VisionConfigurationRegistry.Load(
            store ?? new WindowsVisionConfigurationStore(),
            root);
        if (!loaded.IsValid)
        {
            logger.Error(
                "Vision registry state INVALID code={Code}; Helper startup refused",
                loaded.Code);
            throw new InvalidDataException(
                $"Vision registry state is invalid ({loaded.Code}).");
        }
        if (loaded.IsMissing)
        {
            logger.Information(
                "Vision registry state missing — explicit default-disabled posture code={Code}",
                loaded.Code);
        }
        else
        {
            logger.Information(
                "Vision registry state loaded generation={Generation}",
                loaded.EffectiveGeneration);
        }
        return loaded;
    }

    public static VisionBootstrapResult BuildRuntime(ILogger logger) =>
        BuildRuntime(logger, LoadConfiguration(logger));

    public static VisionBootstrapResult BuildRuntime(
        ILogger logger,
        VisionConfigurationLoadResult configuration) =>
        BuildRuntime(logger, configuration, OperatingSystem.IsWindows());

    internal static VisionBootstrapResult BuildRuntime(
        ILogger logger,
        VisionConfigurationLoadResult configuration,
        bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configuration);
        var runtime = new VisionRuntimeStatusTracker(configuration);
        var opts = configuration.EffectiveOptions.ToOptions();

        if (!opts.Enabled)
        {
            logger.Information(
                "Vision disabled by machine registry state generation={Generation}",
                configuration.EffectiveGeneration);
            return new(null, null, runtime);
        }

        if (!isWindows)
        {
            runtime.RecordPlatformUnsupported();
            logger.Warning("Vision enabled on unsupported platform");
            return new(null, null, runtime);
        }

        try
        {
            var agentOpts = Options.Create(new AgentOptions { Vision = opts });
            // Build/warm OCR first. If the Setup-provisioned cohort or native
            // engine is unavailable, this throws a static-code failure and we
            // never create a controller that can return UIA-only false success.
            IScreenExtractor extractor = ScrubbedExtractorFactory.Create(
                agentOpts,
                logger,
                runtime);
            IScreenCapture capture = BuildLivePmsCapture(agentOpts, logger);
            IScreenStore store = new EncryptedScreenStore(agentOpts, logger);
            var controller = new ScreenCaptureController(capture, store, extractor, logger);
            var pricing = opts.Tesseract.Enabled
                ? new VisionPricingGridReader(extractor, agentOpts, logger)
                : null;
            runtime.RecordReady(opts.Tesseract.Enabled);

            logger.Information(
                "Vision ENABLED — capture={CaptureAvailable}, retention={RetHours}h, cap={Max}, extractor={Ext}",
                capture.IsAvailable, opts.RetentionHours, opts.MaxStoredScreens, extractor.ExtractorId);
            return new(controller, pricing, runtime);
        }
        catch (VisionRuntimeUnavailableException ex)
        {
            runtime.RecordFailure(ex.Code);
            logger.Error(
                "Vision runtime unavailable code={Code}; machine vision fails closed",
                ex.Code);
            return new(null, null, runtime);
        }
        catch (Exception ex)
        {
            if (runtime.Snapshot().Code == VisionRuntimeCodes.VisionStarting)
                runtime.RecordFailure(VisionRuntimeCodes.VisionPipelineInitializationFailed);
            logger.Error(
                "Vision pipeline initialization failed ({ErrorType}); machine vision fails closed",
                ex.GetType().Name);
            return new(null, null, runtime);
        }
    }

    public static ScreenCaptureController? TryBuild(ILogger logger) =>
        BuildRuntime(logger).CaptureController;

    public static ScreenCaptureController? TryBuild(
        ILogger logger,
        VisionConfigurationLoadResult configuration) =>
        BuildRuntime(logger, configuration).CaptureController;

    /// <summary>
    /// Builds the vision-based pricing reader for the PMS box. Unlike the sandbox bootstrap, this IS
    /// allowed on a PioneerRx box — it captures ONLY the Edit-Rx-Item/Supplier-Catalog window (per
    /// lookup, HWND-scoped) to read the cheapest supplier by sight, PHI-scrubbed, on-device. Gated on
    /// the same registry-state opt-in (+ Tesseract enabled) as the observation pipeline. Returns null when
    /// vision is off, OCR is off, non-Windows, or on any construction error → caller stays UIA-only.
    /// </summary>
    public static SuavoAgent.Helper.Workflows.VisionPricingGridReader? TryBuildPricingReader(
        ILogger logger) => BuildRuntime(logger).PricingReader;

    public static SuavoAgent.Helper.Workflows.VisionPricingGridReader? TryBuildPricingReader(
        ILogger logger,
        VisionConfigurationLoadResult configuration) =>
        BuildRuntime(logger, configuration).PricingReader;

    /// <summary>
    /// Sandbox-only bootstrap for WINDOW-SCOPED PrintWindow capture on a NON-PHI box.
    /// Unlike <see cref="TryBuild"/>, this does NOT require machine vision state — sandbox capture is
    /// opt-in per explore_sandbox command, not via an operator config file — but it captures
    /// ONLY the single allowlisted-sandbox window identified by <paramref name="targetHwnd"/>
    /// (PrintWindow is HWND-scoped, so no other window's pixels can leak).
    ///
    /// HIPAA build-time gate: REFUSES construction (returns null) if PioneerRx is installed on
    /// this host. A PMS box must NEVER receive this opt-in-free pipeline — it uses <see cref="TryBuild"/>
    /// (registry state, default off) instead. Also returns null on non-Windows, a zero HWND, or any error.
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
            logger.Warning(
                "VisionBootstrap.TryBuildWindowSandbox: failed to build pipeline ({ErrorType})",
                ex.GetType().Name);
            return null;
        }
    }

    internal static IScreenCapture BuildLivePmsCapture(
        IOptions<AgentOptions> options,
        ILogger logger) => new ApprovedPmsForegroundWindowCapture(options, logger);

}
