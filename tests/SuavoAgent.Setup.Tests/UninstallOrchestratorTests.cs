using Avalonia.Headless.XUnit;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using SuavoAgent.Setup.Gui.Views;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Guards the GUI uninstall flow: the residue/clean decision (FullyClean
/// semantics), the orchestrator's default-path fallback, and the headless
/// XAML-load of the three new views (same Bug-24 class the install views are
/// guarded against). The actual service/dir removal is ServiceInstaller's job
/// and is only exercisable on Windows-with-admin — not re-tested here.
/// </summary>
public sealed class UninstallOrchestratorTests
{
    // ── UninstallResult.FullyClean semantics ───────────────────────────────

    [Fact]
    public void FullyClean_true_only_when_no_residue()
    {
        var r = new ServiceInstaller.UninstallResult
        {
            ServicesRemoved = true,
            ServicesRemaining = 0,
            DataDirRemoved = true,
            InstallDirRemoved = true,
        };

        Assert.True(r.FullyClean);
    }

    [Theory]
    // services still registered → not clean
    [InlineData(1, true, true)]
    // data dir not removed → not clean
    [InlineData(0, false, true)]
    // install dir not removed (locked file) → not clean
    [InlineData(0, true, false)]
    // everything residual → not clean
    [InlineData(2, false, false)]
    public void FullyClean_false_when_any_residue(int servicesRemaining, bool dataRemoved, bool installRemoved)
    {
        var r = new ServiceInstaller.UninstallResult
        {
            ServicesRemoved = true,
            ServicesRemaining = servicesRemaining,
            DataDirRemoved = dataRemoved,
            InstallDirRemoved = installRemoved,
        };

        Assert.False(r.FullyClean);
    }

    // ── Orchestrator default-path fallback (pure helper) ───────────────────

    [Fact]
    public void Defaults_match_console_uninstaller_paths()
    {
        Assert.Equal(@"C:\Program Files\Suavo\Agent", UninstallOrchestrator.DefaultInstallDir);
        Assert.Equal(@"C:\ProgramData\SuavoAgent", UninstallOrchestrator.DefaultDataDir);
    }

    [Fact]
    public void Confirm_viewmodel_surfaces_the_default_target_paths()
    {
        // The confirmation screen must show the operator exactly what gets deleted.
        var vm = new UninstallConfirmViewModel(onConfirm: () => { }, onCancel: () => { });

        Assert.Equal(UninstallOrchestrator.DefaultInstallDir, vm.InstallPath);
        Assert.Equal(UninstallOrchestrator.DefaultDataDir, vm.DataPath);
    }

    [Fact]
    public void Confirm_command_invokes_confirm_not_cancel()
    {
        var confirmed = false;
        var cancelled = false;
        var vm = new UninstallConfirmViewModel(
            onConfirm: () => confirmed = true,
            onCancel: () => cancelled = true);

        vm.ConfirmCommand.Execute(null);

        Assert.True(confirmed);
        Assert.False(cancelled);
    }

    [Fact]
    public void Success_viewmodel_carries_the_clean_machine_message()
    {
        var vm = new UninstallSuccessViewModel(onFinish: () => { });

        Assert.Equal("SuavoAgent removed — this computer is clean.", vm.Message);
    }

    // ── Progress view reuse: uninstall titles render through the same VM ───

    [Fact]
    public void Progress_viewmodel_accepts_custom_uninstall_phase_titles()
    {
        var vm = new ProgressViewModel(() => { }, new[]
        {
            "Stop & remove services",
            "Remove local data",
            "Remove program files",
        })
        {
            Title = "Uninstalling SuavoAgent",
        };

        Assert.Equal(3, vm.Phases.Count);
        Assert.Equal("Stop & remove services", vm.Phases[0].Title);
        Assert.Equal("Uninstalling SuavoAgent", vm.Title);
    }

    // ── Headless XAML-load smoke (Bug-24 class) for the new views ──────────

    [AvaloniaFact]
    public void UninstallConfirmView_Loads_Without_Exception()
    {
        var view = new UninstallConfirmView
        {
            DataContext = new UninstallConfirmViewModel(() => { }, () => { }),
        };
        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public void UninstallSuccessView_Loads_Without_Exception()
    {
        var view = new UninstallSuccessView
        {
            DataContext = new UninstallSuccessViewModel(() => { }),
        };
        Assert.NotNull(view);
    }

    [AvaloniaFact]
    public void WelcomeView_With_Uninstall_Command_Loads_Without_Exception()
    {
        // WelcomeView now binds an UninstallCommand — guard the XAML/binding.
        var view = new WelcomeView
        {
            DataContext = new WelcomeViewModel(() => { }, () => { }),
        };
        Assert.NotNull(view);
    }
}
