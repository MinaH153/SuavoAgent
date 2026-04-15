using SuavoAgent.Core.Intelligence;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Intelligence;

public class FleetDataChannelsTests
{
    [Fact]
    public void ComputeSignals_ProducesAllFourChannels()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-fleet-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var channels = new FleetDataChannels(db);
            var signals = channels.ComputeSignals("test-pharmacy");
            Assert.Equal("test-pharmacy", signals.PharmacyId);
            Assert.NotNull(signals.OrderVolume);
            Assert.NotNull(signals.PickupReadiness);
            Assert.NotNull(signals.BusinessHours);
            Assert.NotNull(signals.Capacity);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void PickupReadiness_ZeroConfidenceInitially()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-fleet2-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var signals = new FleetDataChannels(db).ComputeSignals("test");
            Assert.Equal(0, signals.PickupReadiness.ConfidencePct);
            Assert.Null(signals.PickupReadiness.EstimatedReadyIn);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void BusinessHours_ActiveWhenRunning()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-fleet3-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var signals = new FleetDataChannels(db).ComputeSignals("test");
            Assert.True(signals.BusinessHours.IsCurrentlyActive);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void Capacity_NormalByDefault()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-fleet4-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var signals = new FleetDataChannels(db).ComputeSignals("test");
            Assert.Equal(LoadLevel.Normal, signals.Capacity.CurrentLoadLevel);
        }
        finally { File.Delete(dbPath); }
    }
}
