using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class ProcessObserverTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public ProcessObserverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_procobs_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("test-sess", "pharm-1");
    }

    [Fact]
    public void PmsSignatures_ContainsPioneerRx()
    {
        Assert.True(ProcessObserver.KnownPmsSignatures.ContainsKey("PioneerPharmacy.exe"));
    }

    [Fact]
    public void IsPmsCandidate_KnownProcess_ReturnsTrue()
    {
        Assert.True(ProcessObserver.IsPmsCandidate("PioneerPharmacy.exe"));
        Assert.True(ProcessObserver.IsPmsCandidate("QS1NexGen.exe"));
    }

    [Fact]
    public void IsPmsCandidate_UnknownProcess_ReturnsFalse()
    {
        Assert.False(ProcessObserver.IsPmsCandidate("notepad.exe"));
        Assert.False(ProcessObserver.IsPmsCandidate("chrome.exe"));
    }

    [Fact]
    public void RecordProcess_PersistsToDb()
    {
        var observer = new ProcessObserver(_db, "test-pharmacy-salt", NullLogger<ProcessObserver>.Instance);
        observer.RecordProcess("test-sess", "PioneerPharmacy.exe",
            @"C:\PioneerRx\PioneerPharmacy.exe", "Point of Sale");

        var processes = _db.GetObservedProcesses("test-sess");
        Assert.Single(processes);
        Assert.True(processes[0].IsPmsCandidate);
        // Window title should be scrubbed (Point of Sale has no PHI, so unchanged)
        Assert.Equal("Point of Sale", processes[0].WindowTitleScrubbed);
    }

    [Fact]
    public void RecordProcess_ScrubsPhiFromTitle()
    {
        var observer = new ProcessObserver(_db, "test-pharmacy-salt", NullLogger<ProcessObserver>.Instance);
        observer.RecordProcess("test-sess", "SomeApp.exe",
            @"C:\App\SomeApp.exe", "John Smith - Patient Record");

        var processes = _db.GetObservedProcesses("test-sess");
        Assert.Single(processes);
        Assert.DoesNotContain("John Smith", processes[0].WindowTitleScrubbed!);
    }

    [Fact]
    public void ObserverHealth_ReportsCorrectly()
    {
        var observer = new ProcessObserver(_db, "salt", NullLogger<ProcessObserver>.Instance);
        var health = observer.CheckHealth();
        Assert.Equal("process", health.ObserverName);
        Assert.False(health.IsRunning);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
