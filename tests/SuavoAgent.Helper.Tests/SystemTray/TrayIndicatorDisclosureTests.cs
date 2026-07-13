using System.Reflection;
using System.Runtime.InteropServices;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Companion;
using SuavoAgent.Helper.SystemTray;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemTray;

public sealed class TrayIndicatorDisclosureTests
{
    [Fact]
    public void DisclosureStatesExactWindowAndConditionalBrowserBoundary()
    {
        var disclosure = TrayIndicator.GetDisclosureText();

        Assert.Contains("exact foreground window", disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("signed approval changes", disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("authenticated browser connector", disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("specific URLs are not collected", disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("on-device PHI scrubbing", disclosure, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "Website domain categories visited",
            disclosure,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeShellTooltipAndAboutCopy_AreFixedPrivacySafeText()
    {
        Assert.InRange(TrayIndicator.TooltipText.Length, 1, 127);
        Assert.False(
            PhiPatternGuard.ContainsPotentialPhi(TrayIndicator.TooltipText, out var tooltipPattern),
            tooltipPattern);
        Assert.False(
            PhiPatternGuard.ContainsPotentialPhi(TrayIndicator.AboutTitle, out var titlePattern),
            titlePattern);
        Assert.False(
            PhiPatternGuard.ContainsPotentialPhi(TrayIndicator.GetDisclosureText(), out var disclosurePattern),
            disclosurePattern);
    }

    [Theory]
    [InlineData(CompanionState.Watching, "observation is active")]
    [InlineData(CompanionState.Learning, "observation is active")]
    [InlineData(CompanionState.Working, "Autopilot is active")]
    [InlineData(CompanionState.Paused, "Autopilot is paused")]
    [InlineData(CompanionState.NeedsAttention, "Autopilot is stopped")]
    [InlineData(CompanionState.Offline, "observation is not active")]
    public void NativeShellTooltipMatchesProvedRuntimeState(
        CompanionState state,
        string expected)
    {
        var tooltip = TrayIndicator.TooltipFor(new CompanionPresentation(
            state,
            "ignored fixed title",
            "ignored fixed status",
            CanPause: false,
            CanResume: false,
            CanStop: false));

        Assert.Contains(expected, tooltip, StringComparison.Ordinal);
        Assert.InRange(tooltip.Length, 1, 127);
        Assert.False(PhiPatternGuard.ContainsPotentialPhi(tooltip, out var pattern), pattern);
    }

    [Fact]
    public void MissingPresentationNeverClaimsObservationIsActive()
    {
        var tooltip = TrayIndicator.TooltipFor(null);

        Assert.Equal(TrayIndicator.TooltipText, tooltip);
        Assert.DoesNotContain("observation is active", tooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TrayIndicator_IsBackedByNativeShellNotificationApi()
    {
        var shellBoundary = typeof(TrayIndicator).GetMethod(
            "ShellNotifyIcon",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(shellBoundary);
        Assert.NotNull(shellBoundary!.GetCustomAttribute<DllImportAttribute>());
    }
}
