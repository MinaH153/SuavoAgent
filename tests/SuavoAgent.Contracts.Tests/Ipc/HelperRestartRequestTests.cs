using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Ipc;

/// <summary>
/// The restart sentinel is the only Core→Broker signal that makes the Broker KILL a process,
/// so its validation must fail closed on every malformed/stale/torn input — a bad file must
/// never kill a Helper, and a forgotten file must never kill one minutes later.
/// </summary>
public class HelperRestartRequestTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"suavo-test-{Guid.NewGuid():N}", HelperRestartRequest.FileName);

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var path = TempPath();
        try
        {
            HelperRestartRequest.Write(path, new HelperRestartRequest.Payload(T0, "operator_restart_helper", "operator", "cmd-1"));

            var read = HelperRestartRequest.TryRead(path, T0.AddSeconds(30));

            Assert.NotNull(read);
            Assert.Equal("operator", read!.RequestedBy);
            Assert.Equal("operator_restart_helper", read.Reason);
            Assert.Equal("cmd-1", read.CommandId);
            Assert.True(HelperRestartRequest.IsPending(path, T0.AddSeconds(30)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void StaleRequest_IsRejected()
    {
        var path = TempPath();
        try
        {
            HelperRestartRequest.Write(path, new HelperRestartRequest.Payload(T0, "r", "self_heal", null));

            Assert.Null(HelperRestartRequest.TryRead(path, T0 + HelperRestartRequest.MaxAge + TimeSpan.FromSeconds(1)));
            Assert.False(HelperRestartRequest.IsPending(path, T0.AddMinutes(10)));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FutureDatedRequest_BeyondClockSkew_IsRejected()
    {
        // A request "from the future" (clock tamper / bad write) must not be honored.
        var path = TempPath();
        try
        {
            HelperRestartRequest.Write(path, new HelperRestartRequest.Payload(T0.AddMinutes(5), "r", "operator", null));
            Assert.Null(HelperRestartRequest.TryRead(path, T0));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void SmallForwardSkew_IsTolerated()
    {
        var path = TempPath();
        try
        {
            HelperRestartRequest.Write(path, new HelperRestartRequest.Payload(T0.AddSeconds(30), "r", "operator", null));
            Assert.NotNull(HelperRestartRequest.TryRead(path, T0));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MalformedFile_IsRejected_NeverThrows()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Assert.Null(HelperRestartRequest.TryRead(path, T0));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void MissingFile_IsNotPending()
    {
        Assert.Null(HelperRestartRequest.TryRead(TempPath(), T0));
        Assert.False(HelperRestartRequest.IsPending(TempPath(), T0));
    }

    [Fact]
    public void Write_OverwritesPendingRequest_TwoRestartsCollapseIntoOne()
    {
        var path = TempPath();
        try
        {
            HelperRestartRequest.Write(path, new HelperRestartRequest.Payload(T0, "first", "operator", "a"));
            HelperRestartRequest.Write(path, new HelperRestartRequest.Payload(T0.AddSeconds(10), "second", "self_heal", "b"));

            var read = HelperRestartRequest.TryRead(path, T0.AddSeconds(20));
            Assert.Equal("second", read!.Reason);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TryDelete_IsIdempotent()
    {
        var path = TempPath();
        HelperRestartRequest.TryDelete(path); // absent — must not throw
        HelperRestartRequest.Write(path, new HelperRestartRequest.Payload(T0, "r", "operator", null));
        HelperRestartRequest.TryDelete(path);
        HelperRestartRequest.TryDelete(path);
        Assert.False(File.Exists(path));
        Cleanup(path);
    }

    [Theory]
    [InlineData(0, true)]      // fresh
    [InlineData(119, true)]    // just inside MaxAge (2 min)
    [InlineData(121, false)]   // just past
    public void IsFresh_Boundary(int ageSeconds, bool expected)
    {
        var payload = new HelperRestartRequest.Payload(T0, "r", "operator", null);
        Assert.Equal(expected, HelperRestartRequest.IsFresh(payload, T0.AddSeconds(ageSeconds)));
    }

    private static void Cleanup(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* test cleanup best-effort */ }
    }
}
