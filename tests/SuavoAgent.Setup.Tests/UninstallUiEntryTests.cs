using Avalonia.Headless.XUnit;
using SuavoAgent.Setup.Gui.ViewModels;
using SuavoAgent.Setup.Gui.Views;
using SuavoAgent.Diagnostics.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class UninstallUiEntryTests
{
    [Theory]
    [InlineData("--uninstall-ui")]
    [InlineData("--UNINSTALL-UI")]
    public void UninstallUiSwitch_IsCaseInsensitiveAndDistinctFromHeadlessMode(string value)
    {
        Assert.True(Program.IsUninstallUiMode([value]));
        Assert.False(Program.IsUninstallUiMode(["--uninstall"]));
    }

    [Theory]
    [InlineData("--repair-ui")]
    [InlineData("--REPAIR-UI")]
    public void RepairUiSwitch_IsCaseInsensitiveAndDistinctFromHeadlessMode(string value)
    {
        Assert.True(Program.IsRepairUiMode([value]));
        Assert.False(Program.IsRepairUiMode(["--repair-services"]));
        Assert.False(Program.IsUninstallUiMode([value]));
    }

    [Theory]
    [InlineData("--connect-installed")]
    [InlineData("--CONNECT-INSTALLED")]
    public void InstalledConnectSwitch_IsCaseInsensitiveAndDistinctFromMaintenanceModes(
        string value)
    {
        Assert.True(Program.IsConnectInstalledMode([value]));
        Assert.False(Program.IsRepairUiMode([value]));
        Assert.False(Program.IsUninstallUiMode([value]));
        Assert.False(Program.IsConnectInstalledMode(["--repair-services"]));
    }

    [AvaloniaFact]
    public void SettingsEntry_OpensNativeUninstallConfirmationWithoutPairing()
    {
        var exitCode = -1;
        var viewModel = new MainWindowViewModel(
            code => exitCode = code,
            startInUninstall: true);

        Assert.IsType<UninstallConfirmView>(viewModel.CurrentView);
        Assert.Equal("Uninstall · Confirm", viewModel.StepLabel);
        Assert.Equal(-1, exitCode);
    }

    [AvaloniaFact]
    public void SettingsModifyEntry_OpensNativeRepairConfirmationWithoutPairing()
    {
        var exitCode = -1;
        var viewModel = new MainWindowViewModel(
            code => exitCode = code,
            startInRepair: true);

        Assert.IsType<RepairConfirmView>(viewModel.CurrentView);
        Assert.Equal("Repair services · Confirm", viewModel.StepLabel);
        Assert.Equal(-1, exitCode);
    }

    [Fact]
    public void ProtectedUninstallCleanup_RejectsSameSidUserTempReplacement()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "suavo-uninstall-policy-" + Guid.NewGuid().ToString("N"));
        var commonData = Path.Combine(root, "ProgramData");
        var userTemp = Path.Combine(root, "same-sid-temp");
        var staging = Path.Combine(
            commonData,
            PrivilegedExecutableStaging.DirectoryPrefix + new string('a', 32));
        var fileName = PrivilegedExecutableStaging.UninstallFilePrefix +
                       new string('b', 32) + ".exe";
        var valid = Path.Combine(staging, fileName);
        var userReplaceable = Path.Combine(userTemp, fileName);
        var wrongName = Path.Combine(staging, "SuavoAgent.Maintenance.exe");
        var wrongDirectory = Path.Combine(commonData, "SuavoAgent", fileName);

        Assert.True(UninstallInstaller.IsSafeTemporaryUninstallCopy(
            valid,
            commonData,
            userTemp));
        Assert.False(UninstallInstaller.IsSafeTemporaryUninstallCopy(
            userReplaceable,
            commonData,
            userTemp));
        Assert.False(UninstallInstaller.IsSafeTemporaryUninstallCopy(
            wrongName,
            commonData,
            userTemp));
        Assert.False(UninstallInstaller.IsSafeTemporaryUninstallCopy(
            wrongDirectory,
            commonData,
            userTemp));
    }

    [Fact]
    public void ProtectedCleanup_NeverDeletesAnArbitraryCallerSuppliedDirectory()
    {
        var arbitrary = Path.Combine(
            Path.GetTempPath(),
            "not-suavo-privileged-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(arbitrary);
        try
        {
            PrivilegedExecutableStaging.TryCleanupDirectory(arbitrary);
            Assert.True(Directory.Exists(arbitrary));
        }
        finally
        {
            Directory.Delete(arbitrary);
        }
    }

    [Fact]
    public void VisibleUninstall_HandsOffBeforeAvaloniaStarts()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Setup", "Program.cs"));
        if (!File.Exists(sourcePath)) return;

        var source = File.ReadAllText(sourcePath);
        var handoff = source.IndexOf(
            "UninstallInstaller.TryReExecFromTemp(args)",
            StringComparison.Ordinal);
        var avalonia = source.IndexOf(
            "StartWithClassicDesktopLifetime(args)",
            StringComparison.Ordinal);

        Assert.True(handoff >= 0);
        Assert.True(avalonia > handoff);
    }

    [Fact]
    public async Task HeadlessLocalIntentWithoutAuthenticatedClaimIsNeverRemovalSuccess()
    {
        var exitCode = await UninstallInstaller.RunAsync(
            ["--uninstall", "--silent", "--preserve-data"]);

        Assert.Equal(3, exitCode);
    }
}
