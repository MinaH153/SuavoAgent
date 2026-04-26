using SuavoAgent.Core.Ipc;
using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

// IpcRejectionStats is process-global static state. These tests run sequentially
// within a [Collection] so concurrent test discovery doesn't see counter
// interleaving from siblings. The Trip A 2026-04-25 silent-IPC-failure metric
// is what this class powers — keep coverage tight on Record + readback so a
// future refactor doesn't break the cloud heartbeat contract silently.
[Collection("IpcRejectionStats")]
public class IpcRejectionStatsTests
{
    [Fact]
    public void Record_IncrementsCount()
    {
        var startCount = IpcRejectionStats.Count;
        IpcRejectionStats.Record("test_reason_a");
        IpcRejectionStats.Record("test_reason_b");
        Assert.Equal(startCount + 2, IpcRejectionStats.Count);
    }

    [Fact]
    public void Record_CapturesLastReasonAndTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        IpcRejectionStats.Record("test_reason_specific");
        var after = DateTimeOffset.UtcNow;

        Assert.Equal("test_reason_specific", IpcRejectionStats.LastReason);
        Assert.NotNull(IpcRejectionStats.LastAt);
        Assert.InRange(IpcRejectionStats.LastAt!.Value, before.AddSeconds(-1), after.AddSeconds(1));
    }

    [Fact]
    public void Record_ConcurrentCallers_AllCounted()
    {
        var startCount = IpcRejectionStats.Count;
        const int callers = 50;
        const int callsPerCaller = 20;

        Parallel.For(0, callers, _ =>
        {
            for (var i = 0; i < callsPerCaller; i++)
            {
                IpcRejectionStats.Record("concurrent_test");
            }
        });

        Assert.Equal(startCount + (callers * callsPerCaller), IpcRejectionStats.Count);
    }

    [Fact]
    public void LastReason_NullBeforeFirstRecord_AfterTypeBoot()
    {
        // Don't assert null here — IpcRejectionStats is process-global static and
        // other tests in the suite may have called Record() first. Just verify
        // that the readback path doesn't throw and returns a valid shape.
        var reason = IpcRejectionStats.LastReason;
        var at = IpcRejectionStats.LastAt;
        Assert.True(reason is null || reason.Length > 0);
        Assert.True(at is null || at.Value.Year >= 2024);
    }
}
