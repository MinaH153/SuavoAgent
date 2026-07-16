using Avalonia.Headless.XUnit;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using SuavoAgent.Setup.Gui.Views;
using SuavoAgent.Setup.Maintenance;
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
            ScheduledUninstallTaskAbsent = true,
            ProtocolRegistrationAbsent = true,
            ArpRegistrationAbsent = true,
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
            ScheduledUninstallTaskAbsent = true,
            ProtocolRegistrationAbsent = true,
            ArpRegistrationAbsent = true,
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
    public void Success_viewmodel_carries_the_evidence_retention_message()
    {
        var vm = new UninstallSuccessViewModel(onFinish: () => { });

        Assert.Contains("Retained compliance evidence", vm.Message);
    }

    [Fact]
    public async Task MissingSignedCloudClaimChangesNothingAndNeverFinalizes()
    {
        var finalizerCalls = 0;
        var orchestrator = new UninstallOrchestrator(
            installDir: "/install",
            dataDir: "/data",
            fileExists: _ => false,
            finalize: (_, _, _, _) =>
            {
                finalizerCalls++;
                return Task.FromResult(SelfUninstallFinalizationResult.Finalized());
            });

        var result = await orchestrator.RunAsync(
            new Progress<UninstallOrchestrator.PhaseEvent>(),
            CancellationToken.None);

        Assert.False(result.IsFinalized);
        Assert.Equal("signed_cloud_authority_required", result.Code);
        Assert.Equal(0, finalizerCalls);
        Assert.Null(result.Cleanup);
    }

    [Fact]
    public async Task SignedClaimIsSuccessfulOnlyWithCloudFinalizationAndZeroResidue()
    {
        var cleanup = new ServiceInstaller.UninstallResult
        {
            ServicesRemaining = 0,
            DataDirRemoved = true,
            InstallDirRemoved = true,
            ScheduledUninstallTaskAbsent = true,
            ProtocolRegistrationAbsent = true,
            ArpRegistrationAbsent = true,
        };
        var orchestrator = new UninstallOrchestrator(
            installDir: "/install",
            dataDir: "/data",
            fileExists: _ => true,
            finalize: (_, _, _, _) => Task.FromResult(
                SelfUninstallFinalizationResult.Finalized(cleanup)));

        var result = await orchestrator.RunAsync(
            new Progress<UninstallOrchestrator.PhaseEvent>(),
            CancellationToken.None);

        Assert.True(result.IsFinalized);
        Assert.True(result.Cleanup!.FullyClean);
    }

    [Fact]
    public async Task CloudPendingResultNeverBecomesGuiSuccessAfterLocalCleanup()
    {
        var cleanup = new ServiceInstaller.UninstallResult
        {
            ServicesRemaining = 0,
            DataDirRemoved = true,
            InstallDirRemoved = true,
            ScheduledUninstallTaskAbsent = true,
            ProtocolRegistrationAbsent = true,
            ArpRegistrationAbsent = true,
        };
        var orchestrator = new UninstallOrchestrator(
            installDir: "/install",
            dataDir: "/data",
            fileExists: _ => true,
            finalize: (_, _, _, _) => Task.FromResult(
                SelfUninstallFinalizationResult.Pending(
                    "cloud_completion_pending",
                    cleanup)));

        var result = await orchestrator.RunAsync(
            new Progress<UninstallOrchestrator.PhaseEvent>(),
            CancellationToken.None);

        Assert.False(result.IsFinalized);
        Assert.Equal("cloud_completion_pending", result.Code);
    }

    // ── Progress view reuse: uninstall titles render through the same VM ───

    [Fact]
    public void Progress_viewmodel_accepts_custom_uninstall_phase_titles()
    {
        var vm = new ProgressViewModel(() => { }, new[]
        {
            "Confirm dashboard authority",
            "Remove authorized runtime",
            "Preserve compliance evidence",
            "Confirm cloud completion",
        })
        {
            Title = "Uninstalling SuavoAgent",
        };

        Assert.Equal(4, vm.Phases.Count);
        Assert.Equal("Confirm dashboard authority", vm.Phases[0].Title);
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
