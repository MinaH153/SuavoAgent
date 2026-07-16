using SuavoAgent.Setup.InstallerSupport;
using Xunit;

namespace SuavoAgent.Setup.Tests.InstallerSupport;

public sealed class MsiCleanHostPreflightTests
{
    [Theory]
    [InlineData(null)]
    [InlineData()]
    [InlineData("--msi-assert-clean-install-host", "unexpected")]
    [InlineData("--doctor")]
    public void Runner_rejects_every_non_exact_argument_shape(params string[]? arguments)
    {
        var probed = false;

        var result = MsiCleanHostPreflightRunner.Run(
            arguments,
            isWindows: true,
            () => { probed = true; return false; },
            _ => probed = true);

        Assert.Equal((int)MsiCleanHostPreflightExitCode.InvalidArguments, result);
        Assert.False(probed);
    }

    [Fact]
    public void Recognized_related_product_still_runs_independent_legacy_probe()
    {
        bool? recognizedByProbe = null;

        var result = MsiCleanHostPreflightRunner.Run(
            [MsiCleanHostPreflightRunner.Switch],
            isWindows: true,
            () => true,
            recognized => recognizedByProbe = recognized);

        Assert.Equal((int)MsiCleanHostPreflightExitCode.Success, result);
        Assert.True(recognizedByProbe);
    }

    [Fact]
    public void Fresh_host_must_pass_read_only_clean_probe()
    {
        bool? recognizedByProbe = null;

        var result = MsiCleanHostPreflightRunner.Run(
            [MsiCleanHostPreflightRunner.Switch],
            isWindows: true,
            () => false,
            recognized => recognizedByProbe = recognized);

        Assert.Equal((int)MsiCleanHostPreflightExitCode.Success, result);
        Assert.False(recognizedByProbe);
    }

    [Fact]
    public void Recognized_related_product_cannot_mask_independent_legacy_state()
    {
        var result = MsiCleanHostPreflightRunner.Run(
            [MsiCleanHostPreflightRunner.Switch],
            isWindows: true,
            () => true,
            recognized =>
            {
                Assert.True(recognized);
                throw new MsiLegacyStatePresentException();
            });

        Assert.Equal((int)MsiCleanHostPreflightExitCode.LegacyStatePresent, result);
    }

    [Fact]
    public void Legacy_or_unreadable_state_fails_closed_with_bounded_codes()
    {
        var legacy = MsiCleanHostPreflightRunner.Run(
            [MsiCleanHostPreflightRunner.Switch],
            isWindows: true,
            () => false,
            _ => throw new MsiLegacyStatePresentException());
        var unreadable = MsiCleanHostPreflightRunner.Run(
            [MsiCleanHostPreflightRunner.Switch],
            isWindows: true,
            () => false,
            _ => throw new IOException("Injected probe failure."));

        Assert.Equal((int)MsiCleanHostPreflightExitCode.LegacyStatePresent, legacy);
        Assert.Equal((int)MsiCleanHostPreflightExitCode.ProbeFailed, unreadable);
    }

    [Fact]
    public void Non_windows_host_rejects_before_probes()
    {
        var probed = false;

        var result = MsiCleanHostPreflightRunner.Run(
            [MsiCleanHostPreflightRunner.Switch],
            isWindows: false,
            () => { probed = true; return false; },
            _ => probed = true);

        Assert.Equal((int)MsiCleanHostPreflightExitCode.UnsupportedHost, result);
        Assert.False(probed);
    }

    [Fact]
    public void Broker_process_classifier_exempts_only_related_msi_owned_path()
    {
        const string canonical =
            @"C:\Program Files\Suavo\Agent\SuavoAgent.Broker.exe";
        const string developer =
            @"C:\Users\Nadim\suavo-publish\Broker\SuavoAgent.Broker.exe";

        Assert.True(WindowsMsiCleanHostProbe.IsAllowedBrokerProcessPath(
            canonical,
            hasRecognizedRelatedProduct: true,
            canonicalBrokerPath: canonical));
        Assert.False(WindowsMsiCleanHostProbe.IsAllowedBrokerProcessPath(
            developer,
            hasRecognizedRelatedProduct: true,
            canonicalBrokerPath: canonical));
        Assert.False(WindowsMsiCleanHostProbe.IsAllowedBrokerProcessPath(
            canonical,
            hasRecognizedRelatedProduct: false,
            canonicalBrokerPath: canonical));
    }

    [Fact]
    public void Shortcut_classifier_matches_only_exact_historical_launch_target()
    {
        const string developer =
            @"C:\Users\Nadim\suavo-publish\Broker\SuavoAgent.Broker.exe";

        Assert.True(WindowsMsiCleanHostProbe.IsExactLegacyShortcutTarget(
            developer,
            string.Empty,
            @"C:\Windows"));
        Assert.True(WindowsMsiCleanHostProbe.IsExactLegacyShortcutTarget(
            @"C:\Windows\System32\cmd.exe",
            $"/c start \"\" \"{developer}\"",
            @"C:\Windows"));
        Assert.False(WindowsMsiCleanHostProbe.IsExactLegacyShortcutTarget(
            @"C:\Tools\SuavoAgent.Broker.exe",
            string.Empty,
            @"C:\Windows"));
        Assert.False(WindowsMsiCleanHostProbe.IsExactLegacyShortcutTarget(
            @"C:\Windows\System32\cmd.exe",
            "/c echo unrelated",
            @"C:\Windows"));
    }

    [Theory]
    [InlineData(@"C:\Users\Nadim\Desktop", DriveType.Fixed, false, true)]
    [InlineData(@"D:\Profiles\Nadim\Desktop", DriveType.Fixed, false, true)]
    [InlineData(@"\\server\profiles\Nadim\Desktop", DriveType.Network, false, false)]
    [InlineData(@"Z:\Profiles\Nadim\Desktop", DriveType.Network, false, false)]
    [InlineData(@"D:\Profiles\Nadim\Desktop", DriveType.Fixed, true, false)]
    [InlineData(@"D:\Profiles\..\Nadim\Desktop", DriveType.Fixed, false, false)]
    [InlineData("relative", DriveType.Fixed, false, false)]
    [InlineData("", DriveType.Fixed, false, false)]
    public void Shortcut_root_filter_accepts_any_bounded_fixed_local_drive(
        string path,
        DriveType driveType,
        bool pathContainsReparsePoint,
        bool expected) =>
        Assert.Equal(
            expected,
            WindowsMsiCleanHostProbe.IsBoundedLocalShortcutRoot(
                path,
                driveType,
                pathContainsReparsePoint));
}
