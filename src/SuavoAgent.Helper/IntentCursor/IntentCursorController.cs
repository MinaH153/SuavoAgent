using Serilog;
using SuavoAgent.Contracts.Ipc;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SuavoAgent.Helper.IntentCursor;

public delegate bool IntentCursorAnchorResolver(string anchor, out double x, out double y);

public sealed record IntentCursorRenderRequest(
    int X,
    int Y,
    int DurationMs,
    int DiameterPx,
    double Opacity,
    string Tone);

public sealed record IntentCursorResult(
    bool Accepted,
    string? ErrorCode,
    IntentCursorRenderRequest? Rendered)
{
    public static IntentCursorResult Rejected(string code) => new(false, code, null);
    public static IntentCursorResult Shown(IntentCursorRenderRequest rendered) => new(true, null, rendered);
}

public interface IIntentCursorRenderer
{
    Task ShowAsync(IntentCursorRenderRequest request, CancellationToken ct);
}

public sealed class IntentCursorController
{
    private readonly IIntentCursorRenderer _renderer;
    private readonly ILogger _logger;
    private readonly IntentCursorAnchorResolver _anchorResolver;

    public IntentCursorController(
        IIntentCursorRenderer renderer,
        ILogger logger,
        IntentCursorAnchorResolver? anchorResolver = null)
    {
        _renderer = renderer;
        _logger = logger;
        _anchorResolver = anchorResolver ?? IntentCursorAnchorResolvers.TryResolve;
    }

    public async Task<IntentCursorResult> ShowAsync(IntentCursorRequest request, CancellationToken ct)
    {
        if (!IntentCursorPolicy.TryNormalize(request, out var render, out var errorCode, _anchorResolver))
        {
            return IntentCursorResult.Rejected(errorCode ?? "invalid_intent_cursor");
        }

        try
        {
            await _renderer.ShowAsync(render!, ct).ConfigureAwait(false);
            return IntentCursorResult.Shown(render!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return IntentCursorResult.Rejected("cancelled");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Intent cursor renderer failed");
            return IntentCursorResult.Rejected("renderer_error");
        }
    }
}

public static class IntentCursorPolicy
{
    public const int MinCoordinate = -65535;
    public const int MaxCoordinate = 65535;
    public const int MinDurationMs = 150;
    public const int MaxDurationMs = 5000;
    public const int DefaultDurationMs = 1200;
    public const int FadeInMs = 120;
    public const int FadeOutMs = 220;
    public const int MinDiameterPx = 16;
    public const int MaxDiameterPx = 96;
    public const int DefaultDiameterPx = 34;
    public const double MinOpacity = 0.25;
    public const double MaxOpacity = 0.9;
    public const double DefaultOpacity = 0.72;

    public static bool TryNormalize(
        IntentCursorRequest? request,
        out IntentCursorRenderRequest? render,
        out string? errorCode,
        IntentCursorAnchorResolver? anchorResolver = null)
    {
        render = null;
        errorCode = null;

        if (request is null)
        {
            errorCode = "missing_request";
            return false;
        }

        if (!string.Equals(
                request.CoordinateSpace,
                IntentCursorCoordinateSpaces.Screen,
                StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "unsupported_coordinate_space";
            return false;
        }

        var hasAnchor = !string.IsNullOrWhiteSpace(request.Anchor);
        var hasCoordinates = request.X.HasValue || request.Y.HasValue;
        double x;
        double y;

        if (hasAnchor && hasCoordinates)
        {
            errorCode = "ambiguous_coordinates";
            return false;
        }

        if (hasAnchor)
        {
            if (!string.Equals(request.Anchor, IntentCursorAnchors.PrimaryCenter, StringComparison.Ordinal))
            {
                errorCode = "invalid_anchor";
                return false;
            }

            if (anchorResolver is null || !anchorResolver(request.Anchor!, out x, out y))
            {
                errorCode = "anchor_unavailable";
                return false;
            }
        }
        else
        {
            if (!request.X.HasValue || !request.Y.HasValue)
            {
                errorCode = "invalid_coordinates";
                return false;
            }

            x = request.X.Value;
            y = request.Y.Value;
        }

        if (!double.IsFinite(x) ||
            !double.IsFinite(y) ||
            x < MinCoordinate ||
            x > MaxCoordinate ||
            y < MinCoordinate ||
            y > MaxCoordinate)
        {
            errorCode = "invalid_coordinates";
            return false;
        }

        var duration = request.DurationMs <= 0
            ? DefaultDurationMs
            : Math.Clamp(request.DurationMs, MinDurationMs, MaxDurationMs);
        var diameter = request.DiameterPx <= 0
            ? DefaultDiameterPx
            : Math.Clamp(request.DiameterPx, MinDiameterPx, MaxDiameterPx);
        var opacity = double.IsFinite(request.Opacity)
            ? Math.Clamp(request.Opacity, MinOpacity, MaxOpacity)
            : DefaultOpacity;

        render = new IntentCursorRenderRequest(
            X: RoundCoordinate(x),
            Y: RoundCoordinate(y),
            DurationMs: duration,
            DiameterPx: diameter,
            Opacity: opacity,
            Tone: NormalizeTone(request.Tone));
        return true;
    }

    public static double OpacityAt(double requestedOpacity, int elapsedMs, int durationMs)
    {
        if (durationMs <= 0 || elapsedMs <= 0 || elapsedMs >= durationMs)
            return 0;

        var opacity = Math.Clamp(requestedOpacity, MinOpacity, MaxOpacity);
        var fadeIn = Math.Min(FadeInMs, Math.Max(1, durationMs / 3));
        var fadeOut = Math.Min(FadeOutMs, Math.Max(1, durationMs / 3));
        var scale = 1.0d;

        if (elapsedMs < fadeIn)
        {
            scale = Math.Min(scale, elapsedMs / (double)fadeIn);
        }

        var remainingMs = durationMs - elapsedMs;
        if (remainingMs < fadeOut)
        {
            scale = Math.Min(scale, Math.Max(0, remainingMs / (double)fadeOut));
        }

        return opacity * Math.Clamp(scale, 0, 1);
    }

    private static int RoundCoordinate(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static string NormalizeTone(string? tone) =>
        tone switch
        {
            IntentCursorTones.Agent => IntentCursorTones.Agent,
            IntentCursorTones.Attention => IntentCursorTones.Attention,
            IntentCursorTones.Success => IntentCursorTones.Success,
            IntentCursorTones.Warning => IntentCursorTones.Warning,
            _ => IntentCursorTones.Agent,
    };
}

public static class IntentCursorAnchorResolvers
{
    public static bool TryResolve(string anchor, out double x, out double y)
    {
        x = 0;
        y = 0;

        if (!string.Equals(anchor, IntentCursorAnchors.PrimaryCenter, StringComparison.Ordinal))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return WindowsPrimaryDisplay.TryGetCenter(out x, out y);
    }
}

[SupportedOSPlatform("windows")]
internal static class WindowsPrimaryDisplay
{
    public static bool TryGetCenter(out double x, out double y)
    {
        x = 0;
        y = 0;

        var width = GetSystemMetrics(SM_CXSCREEN);
        var height = GetSystemMetrics(SM_CYSCREEN);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        x = width / 2.0d;
        y = height / 2.0d;
        return true;
    }

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}

public sealed class NullIntentCursorRenderer : IIntentCursorRenderer
{
    private readonly ILogger _logger;

    public NullIntentCursorRenderer(ILogger logger)
    {
        _logger = logger;
    }

    public Task ShowAsync(IntentCursorRenderRequest request, CancellationToken ct)
    {
        _logger.Debug(
            "Intent cursor accepted on non-Windows/no-op renderer ({X},{Y}) duration={DurationMs}ms",
            request.X, request.Y, request.DurationMs);
        return Task.CompletedTask;
    }
}

public static class IntentCursorBootstrap
{
    public static IntentCursorController Build(ILogger logger)
    {
        IIntentCursorRenderer renderer = OperatingSystem.IsWindows()
            ? new WindowsIntentCursorRenderer(logger)
            : new NullIntentCursorRenderer(logger);
        return new IntentCursorController(renderer, logger);
    }
}
