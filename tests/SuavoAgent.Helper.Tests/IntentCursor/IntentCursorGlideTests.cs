using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.IntentCursor;
using Xunit;

namespace SuavoAgent.Helper.Tests.IntentCursor;

// The "agentic glide": instead of a static halo that appears at one spot, the
// intent cursor can travel from a start point to a target, interpolated every
// frame on-box with easing so it reads like ChatGPT/Claude computer-use. These
// guard the PURE pieces that drive that motion (easing curve, integer
// interpolation, and the to-target resolution in TryNormalize). The animation
// itself runs in WindowsIntentCursorRenderer; these keep the math honest.
public class IntentCursorGlideTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 0.5)]   // ease-in-out is symmetric through the midpoint
    [InlineData(1.0, 1.0)]
    public void Ease_InOutCubic_HitsEndpointsAndMidpoint(double progress, double expected)
    {
        Assert.Equal(expected, IntentCursorPolicy.Ease(IntentCursorEasings.EaseInOutCubic, progress), precision: 3);
    }

    [Fact]
    public void Ease_InOutCubic_AcceleratesThenDecelerates()
    {
        // Below the midpoint the eased value trails linear progress (slow start);
        // above it, it leads (fast middle, slow finish). That S-curve is what
        // makes the glide feel intentional rather than mechanical.
        Assert.True(IntentCursorPolicy.Ease(IntentCursorEasings.EaseInOutCubic, 0.25) < 0.25);
        Assert.True(IntentCursorPolicy.Ease(IntentCursorEasings.EaseInOutCubic, 0.75) > 0.75);
    }

    [Fact]
    public void Ease_ClampsProgressOutsideUnitInterval()
    {
        Assert.Equal(0.0, IntentCursorPolicy.Ease(IntentCursorEasings.EaseInOutCubic, -3.0), precision: 3);
        Assert.Equal(1.0, IntentCursorPolicy.Ease(IntentCursorEasings.EaseInOutCubic, 4.0), precision: 3);
    }

    [Fact]
    public void Ease_Linear_IsIdentity()
    {
        Assert.Equal(0.42, IntentCursorPolicy.Ease(IntentCursorEasings.Linear, 0.42), precision: 3);
    }

    [Fact]
    public void Ease_UnknownName_FallsBackToDefaultCurve()
    {
        // Unknown easing must not throw mid-render; it falls back to the default.
        Assert.Equal(
            IntentCursorPolicy.Ease(IntentCursorEasings.EaseInOutCubic, 0.3),
            IntentCursorPolicy.Ease("spinny-bouncy", 0.3),
            precision: 3);
    }

    [Theory]
    [InlineData(0, 100, 0.0, 0)]
    [InlineData(0, 100, 1.0, 100)]
    [InlineData(0, 100, 0.5, 50)]
    [InlineData(100, 0, 0.25, 75)]   // travels backward too
    [InlineData(40, 41, 0.5, 41)]    // rounds away from zero
    public void Lerp_InterpolatesAndRoundsToPixel(int from, int to, double t, int expected)
    {
        Assert.Equal(expected, IntentCursorPolicy.Lerp(from, to, t));
    }

    [Fact]
    public void Policy_ResolvesGlideTargetCoordinates()
    {
        var ok = IntentCursorPolicy.TryNormalize(
            new IntentCursorRequest(X: 100, Y: 120, ToX: 800, ToY: 600, DurationMs: 1400),
            out var render,
            out var errorCode);

        Assert.True(ok);
        Assert.Null(errorCode);
        Assert.NotNull(render);
        Assert.Equal(100, render.X);
        Assert.Equal(120, render.Y);
        Assert.Equal(800, render.ToX);
        Assert.Equal(600, render.ToY);
    }

    [Fact]
    public void Policy_NoTarget_LeavesGlideNullForStaticBackCompat()
    {
        var ok = IntentCursorPolicy.TryNormalize(
            new IntentCursorRequest(100, 200),
            out var render,
            out _);

        Assert.True(ok);
        Assert.NotNull(render);
        Assert.Null(render.ToX);
        Assert.Null(render.ToY);
    }

    [Fact]
    public void Policy_ResolvesGlideTargetAnchor()
    {
        var ok = IntentCursorPolicy.TryNormalize(
            new IntentCursorRequest(
                X: 10, Y: 10,
                ToAnchor: IntentCursorAnchors.PrimaryCenter,
                DurationMs: 1200),
            out var render,
            out var errorCode,
            (string anchor, out double x, out double y) =>
            {
                x = 960;
                y = 540;
                return true;
            });

        Assert.True(ok);
        Assert.Null(errorCode);
        Assert.NotNull(render);
        Assert.Equal(960, render.ToX);
        Assert.Equal(540, render.ToY);
    }

    [Fact]
    public void Policy_RejectsGlideTargetAnchorMixedWithCoordinates()
    {
        var ok = IntentCursorPolicy.TryNormalize(
            new IntentCursorRequest(
                X: 10, Y: 10,
                ToX: 800, ToY: 600,
                ToAnchor: IntentCursorAnchors.PrimaryCenter),
            out var render,
            out var errorCode,
            (string _, out double x, out double y) =>
            {
                x = 0;
                y = 0;
                return true;
            });

        Assert.False(ok);
        Assert.Null(render);
        Assert.Equal("ambiguous_target_coordinates", errorCode);
    }

    [Fact]
    public void Policy_RejectsNonFiniteGlideTarget()
    {
        var ok = IntentCursorPolicy.TryNormalize(
            new IntentCursorRequest(X: 10, Y: 10, ToX: double.NaN, ToY: 600),
            out var render,
            out var errorCode);

        Assert.False(ok);
        Assert.Null(render);
        Assert.Equal("invalid_target_coordinates", errorCode);
    }
}
