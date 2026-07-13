using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class MsiLegacyInteractiveRetirementRunnerTests
{
    [Fact]
    public void Commit_switch_retires_legacy_launch_without_pairing_entry()
    {
        var calls = 0;

        var result = MsiLegacyInteractiveRetirementRunner.Run(
            [MsiLegacyInteractiveRetirementRunner.Switch],
            isWindows: true,
            () =>
            {
                calls++;
                return new(true, 1, 1, 2, false);
            });

        Assert.Equal((int)MsiLegacyInteractiveRetirementExitCode.Success, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Remaining_exact_launch_fails_the_commit_boundary()
    {
        var result = MsiLegacyInteractiveRetirementRunner.Run(
            [MsiLegacyInteractiveRetirementRunner.Switch],
            isWindows: true,
            () => new(false, 0, 0, 1, true));

        Assert.Equal(
            (int)MsiLegacyInteractiveRetirementExitCode.CleanupFailed,
            result);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Every_non_fixed_argument_shape_is_rejected_before_cleanup(
        string[]? arguments)
    {
        var called = false;

        var result = MsiLegacyInteractiveRetirementRunner.Run(
            arguments,
            isWindows: true,
            () =>
            {
                called = true;
                return new(true, 0, 0, 0, false);
            });

        Assert.Equal(
            (int)MsiLegacyInteractiveRetirementExitCode.InvalidArguments,
            result);
        Assert.False(called);
    }

    [Fact]
    public void Unsupported_host_is_rejected_before_cleanup()
    {
        var called = false;

        var result = MsiLegacyInteractiveRetirementRunner.Run(
            [MsiLegacyInteractiveRetirementRunner.Switch],
            isWindows: false,
            () =>
            {
                called = true;
                return new(true, 0, 0, 0, false);
            });

        Assert.Equal(
            (int)MsiLegacyInteractiveRetirementExitCode.UnsupportedHost,
            result);
        Assert.False(called);
        Assert.True(MsiLegacyInteractiveRetirementRunner.IsRequested(
            ["--MSI-RETIRE-LEGACY-INTERACTIVE"]));
        Assert.False(MsiLegacyInteractiveRetirementRunner.IsRequested(
            ["--connect-installed"]));
    }

    public static TheoryData<string[]?> InvalidArguments => new()
    {
        null,
        Array.Empty<string>(),
        new[] { MsiLegacyInteractiveRetirementRunner.Switch, "unexpected" },
        new[] { "--connect-installed" },
    };
}
