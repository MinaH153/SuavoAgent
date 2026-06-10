using System.Linq;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// The remodeled install progress: the brain is a first-class phase with a
/// percent + rotating playful captions, and the default phase order must match
/// InstallOrchestrator.Phase (the int cast indexes the list).
/// </summary>
public sealed class ProgressViewModelTests
{
    [Fact]
    public void DefaultPhases_MatchOrchestratorOrder_IncludingTheBrain()
    {
        var vm = new ProgressViewModel(() => { });
        Assert.Equal(4, vm.Phases.Count);
        Assert.Contains("brain", vm.Phases[2].Title, System.StringComparison.OrdinalIgnoreCase);
        // Orchestrator enum: Download=0, WriteConfig=1, InstallBrain=2, InstallServices=3.
        Assert.Equal(2, (int)Gui.Services.InstallOrchestrator.Phase.InstallBrain);
    }

    [Fact]
    public void BrainPhaseProgress_SetsPercent_AndRotatingCaption()
    {
        var vm = new ProgressViewModel(() => { });
        vm.MarkPhase(2, PhaseState.Running);

        vm.UpdatePhaseProgress("Installing the SuavoAgent brain", 5);
        Assert.Equal(5, vm.Phases[2].Percent);
        Assert.Equal(ProgressViewModel.BrainCaptions[0], vm.Phases[2].Caption);

        vm.UpdatePhaseProgress("Installing the SuavoAgent brain", 95);
        Assert.Equal(ProgressViewModel.BrainCaptions[^1], vm.Phases[2].Caption);
    }

    [Fact]
    public void BrainCaptions_CoverTheFullPercentRange_InOrder()
    {
        // Every percent maps to a valid caption; buckets are monotonically ordered.
        var seen = Enumerable.Range(0, 101).Select(ProgressViewModel.BrainCaptionFor).Distinct().ToList();
        Assert.Equal(ProgressViewModel.BrainCaptions, seen);
    }

    [Fact]
    public void NonBrainPhase_GetsPercent_ButNoCaption()
    {
        var vm = new ProgressViewModel(() => { });
        vm.MarkPhase(0, PhaseState.Running);
        vm.UpdatePhaseProgress("Downloading SuavoAgent binaries", 40);
        Assert.Equal(40, vm.Phases[0].Percent);
        Assert.Equal(string.Empty, vm.Phases[0].Caption);
    }

    [Fact]
    public void PercentLabel_EmptyAtZero_FormattedOtherwise()
    {
        var phase = new PhaseItem("x");
        Assert.Equal(string.Empty, phase.PercentLabel);
        phase.Percent = 42;
        Assert.Equal("42%", phase.PercentLabel);
    }
}
