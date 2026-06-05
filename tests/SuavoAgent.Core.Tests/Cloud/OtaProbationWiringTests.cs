using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// File-level wiring of the OTA crash-loop guard: the probation store (atomic + boot counter), the
/// all-or-nothing downgrade restore, and the P1 cleanup guard that preserves the rollback set during
/// probation. Real binaries/restart are box-validated; these pin the file operations deterministically.
/// </summary>
public sealed class OtaProbationWiringTests : IDisposable
{
    private readonly string _dir;
    private static readonly DateTimeOffset T0 = new(2026, 6, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] Binaries = { "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe" };

    public OtaProbationWiringTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ota-probation-test-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    // --- OtaProbationStore ---------------------------------------------------

    [Fact]
    public void Begin_ThenRead_ReturnsRecord_WithZeroBoots()
    {
        var store = new OtaProbationStore(_dir);
        store.Begin("1.2.3", T0);

        Assert.True(store.HasProbation);
        var rec = store.Read();
        Assert.Equal("1.2.3", rec!.Version);
        Assert.Equal(0, rec.BootsSinceSwap);
    }

    [Fact]
    public void IncrementBoots_PersistsAcrossReads()
    {
        var store = new OtaProbationStore(_dir);
        store.Begin("1.0.0", T0);

        Assert.Equal(1, store.IncrementBoots());
        Assert.Equal(2, new OtaProbationStore(_dir).IncrementBoots()); // fresh instance reads from disk
        Assert.Equal(2, new OtaProbationStore(_dir).Read()!.BootsSinceSwap);
    }

    [Fact]
    public void IncrementBoots_NoRecord_Throws_FailClosed()
    {
        Assert.Throws<InvalidOperationException>(() => new OtaProbationStore(_dir).IncrementBoots());
    }

    [Fact]
    public void Clear_RemovesFlag()
    {
        var store = new OtaProbationStore(_dir);
        store.Begin("1.0.0", T0);
        store.Clear();
        Assert.False(store.HasProbation);
        Assert.Null(store.Read());
    }

    [Fact]
    public void Read_CorruptFlag_ReturnsNull_FailOpen()
    {
        File.WriteAllText(Path.Combine(_dir, OtaProbationStore.FileName), "{ not valid json ");
        Assert.Null(new OtaProbationStore(_dir).Read());
    }

    // --- RestoreOldBinaries (downgrade) --------------------------------------

    private void SeedSwappedSet(string currentContent, string oldContent)
    {
        foreach (var bin in Binaries)
        {
            File.WriteAllText(Path.Combine(_dir, bin), currentContent);
            File.WriteAllText(Path.Combine(_dir, bin + ".old"), oldContent);
        }
    }

    [Fact]
    public void RestoreOldBinaries_FullOldSet_RestoresGood_AndCleansArtifacts()
    {
        SeedSwappedSet(currentContent: "bad-new", oldContent: "good-old");

        var ok = SelfUpdater.RestoreOldBinaries(_dir, NullLogger.Instance);

        Assert.True(ok);
        foreach (var bin in Binaries)
        {
            Assert.Equal("good-old", File.ReadAllText(Path.Combine(_dir, bin)));     // current is now last-good
            Assert.False(File.Exists(Path.Combine(_dir, bin + ".old")));              // .old consumed
            Assert.False(File.Exists(Path.Combine(_dir, bin + ".bad")));              // bad set discarded
        }
    }

    [Fact]
    public void RestoreOldBinaries_MissingOldSet_ReturnsFalse_ChangesNothing()
    {
        // Only one .old present ⇒ all-or-nothing refuses (no version skew).
        File.WriteAllText(Path.Combine(_dir, "SuavoAgent.Core.exe"), "bad-new");
        File.WriteAllText(Path.Combine(_dir, "SuavoAgent.Core.exe.old"), "good-old");

        var ok = SelfUpdater.RestoreOldBinaries(_dir, NullLogger.Instance);

        Assert.False(ok);
        Assert.Equal("bad-new", File.ReadAllText(Path.Combine(_dir, "SuavoAgent.Core.exe"))); // untouched
    }

    // --- CleanupOldBinaries probation guard (the P1) -------------------------

    [Fact]
    public void CleanupOldBinaries_NoProbation_DeletesOldSet()
    {
        foreach (var bin in Binaries)
            File.WriteAllText(Path.Combine(_dir, bin + ".old"), "x");

        SelfUpdater.CleanupOldBinaries(_dir, NullLogger.Instance);

        foreach (var bin in Binaries)
            Assert.False(File.Exists(Path.Combine(_dir, bin + ".old")));
    }

    [Fact]
    public void CleanupOldBinaries_DuringProbation_PreservesRollbackSet()
    {
        foreach (var bin in Binaries)
            File.WriteAllText(Path.Combine(_dir, bin + ".old"), "x");
        new OtaProbationStore(_dir).Begin("1.0.0", T0); // on probation ⇒ .old must survive

        SelfUpdater.CleanupOldBinaries(_dir, NullLogger.Instance);

        foreach (var bin in Binaries)
            Assert.True(File.Exists(Path.Combine(_dir, bin + ".old")), $"{bin}.old must be preserved during probation");
    }

    // --- HandleProbationOnStartup + ConfirmHealthyAndReap --------------------

    private void SetBoots(int boots)
    {
        var store = new OtaProbationStore(_dir);
        store.Begin("1.0.0", T0);
        for (var i = 0; i < boots; i++) store.IncrementBoots();
    }

    [Fact]
    public void HandleProbationOnStartup_NoProbation_ReturnsNoProbation()
    {
        Assert.Equal(SelfUpdater.ProbationStartupAction.NoProbation,
            SelfUpdater.HandleProbationOnStartup(_dir, maxProbationBoots: 3, NullLogger.Instance));
    }

    [Fact]
    public void HandleProbationOnStartup_UnderBudget_IncrementsAndContinues()
    {
        SetBoots(0);
        var action = SelfUpdater.HandleProbationOnStartup(_dir, maxProbationBoots: 3, NullLogger.Instance);

        Assert.Equal(SelfUpdater.ProbationStartupAction.Continue, action);
        Assert.Equal(1, new OtaProbationStore(_dir).Read()!.BootsSinceSwap); // incremented + persisted
    }

    [Fact]
    public void HandleProbationOnStartup_CrashLoop_WithOldSet_RestoresAndRestarts()
    {
        SeedSwappedSet(currentContent: "bad-new", oldContent: "good-old");
        SetBoots(3); // next increment ⇒ 4 > max(3) ⇒ downgrade

        var action = SelfUpdater.HandleProbationOnStartup(_dir, maxProbationBoots: 3, NullLogger.Instance);

        Assert.Equal(SelfUpdater.ProbationStartupAction.RestartForDowngrade, action);
        Assert.Equal("good-old", File.ReadAllText(Path.Combine(_dir, "SuavoAgent.Core.exe"))); // restored
        Assert.False(new OtaProbationStore(_dir).HasProbation); // flag cleared
    }

    [Fact]
    public void HandleProbationOnStartup_CrashLoop_NoOldSet_Escalates()
    {
        // current binaries but NO .old set ⇒ can't downgrade.
        foreach (var bin in Binaries) File.WriteAllText(Path.Combine(_dir, bin), "bad-new");
        SetBoots(3);

        var action = SelfUpdater.HandleProbationOnStartup(_dir, maxProbationBoots: 3, NullLogger.Instance);

        Assert.Equal(SelfUpdater.ProbationStartupAction.EscalateNoRollback, action);
    }

    [Fact]
    public void ConfirmHealthyAndReap_ClearsProbation_AndReapsOldSet()
    {
        foreach (var bin in Binaries) File.WriteAllText(Path.Combine(_dir, bin + ".old"), "x");
        new OtaProbationStore(_dir).Begin("1.0.0", T0);

        SelfUpdater.ConfirmHealthyAndReap(_dir, NullLogger.Instance);

        Assert.False(new OtaProbationStore(_dir).HasProbation);
        foreach (var bin in Binaries)
            Assert.False(File.Exists(Path.Combine(_dir, bin + ".old"))); // reaped after commit
    }
}
