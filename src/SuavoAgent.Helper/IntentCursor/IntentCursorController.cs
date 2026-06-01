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
    string Tone,
    // Glide target. Null => static halo at (X,Y); set => animate (X,Y)->(ToX,ToY).
    int? ToX = null,
    int? ToY = null,
    string Easing = IntentCursorEasings.Default);

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

        // Optional glide target. Mirrors the start-point resolution (anchor XOR
        // raw coords) with distinct error codes so a bad target is diagnosable.
        // Absent => static halo (back-compat).
        int? toX = null;
        int? toY = null;
        var hasToAnchor = !string.IsNullOrWhiteSpace(request.ToAnchor);
        var hasToCoords = request.ToX.HasValue || request.ToY.HasValue;

        if (hasToAnchor && hasToCoords)
        {
            errorCode = "ambiguous_target_coordinates";
            return false;
        }

        if (hasToAnchor || hasToCoords)
        {
            double tx;
            double ty;

            if (hasToAnchor)
            {
                if (!string.Equals(request.ToAnchor, IntentCursorAnchors.PrimaryCenter, StringComparison.Ordinal))
                {
                    errorCode = "invalid_target_anchor";
                    return false;
                }

                if (anchorResolver is null || !anchorResolver(request.ToAnchor!, out tx, out ty))
                {
                    errorCode = "target_anchor_unavailable";
                    return false;
                }
            }
            else
            {
                if (!request.ToX.HasValue || !request.ToY.HasValue)
                {
                    errorCode = "invalid_target_coordinates";
                    return false;
                }

                tx = request.ToX.Value;
                ty = request.ToY.Value;
            }

            if (!double.IsFinite(tx) ||
                !double.IsFinite(ty) ||
                tx < MinCoordinate ||
                tx > MaxCoordinate ||
                ty < MinCoordinate ||
                ty > MaxCoordinate)
            {
                errorCode = "invalid_target_coordinates";
                return false;
            }

            toX = RoundCoordinate(tx);
            toY = RoundCoordinate(ty);
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
            Tone: NormalizeTone(request.Tone),
            ToX: toX,
            ToY: toY,
            Easing: NormalizeEasing(request.Easing));
        return true;
    }

    /// <summary>
    /// Eases normalized progress (0..1) for the glide path. ease-in-out-cubic is
    /// the default S-curve (slow start, fast middle, soft landing); linear is
    /// available for mechanical motion. Unknown names fall back to the default
    /// so a bad payload never throws mid-render.
    /// </summary>
    public static double Ease(string easing, double progress)
    {
        var t = Math.Clamp(progress, 0.0, 1.0);
        return easing switch
        {
            IntentCursorEasings.Linear => t,
            _ => t < 0.5
                ? 4.0 * t * t * t
                : 1.0 - Math.Pow(-2.0 * t + 2.0, 3) / 2.0,
        };
    }

    /// <summary>Pixel-rounded linear interpolation between two integer coordinates.</summary>
    public static int Lerp(int from, int to, double t) =>
        (int)Math.Round(from + (to - from) * t, MidpointRounding.AwayFromZero);

    private static string NormalizeEasing(string? easing) =>
        easing switch
        {
            IntentCursorEasings.Linear => IntentCursorEasings.Linear,
            IntentCursorEasings.EaseInOutCubic => IntentCursorEasings.EaseInOutCubic,
            _ => IntentCursorEasings.Default,
        };

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
