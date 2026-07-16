using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public sealed class UpdateCommandReceiptTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"suavo-update-receipt-{Guid.NewGuid():N}.db");

    [Fact]
    public void ExactCommandAndNonceReplayConvergesButAnyRebindingFailsClosed()
    {
        using var db = new AgentStateDb(_path);
        const string commandId = "11111111-1111-4111-8111-111111111111";
        const string nonce = "22222222-2222-4222-8222-222222222222";

        var first = db.RegisterUpdateCommandReceipt(commandId, nonce, "hash-a", "3.15.8");
        var replay = db.RegisterUpdateCommandReceipt(commandId, nonce, "hash-a", "3.15.8");
        var rebound = db.RegisterUpdateCommandReceipt(commandId, nonce, "hash-b", "3.15.8");

        Assert.True(first.Accepted);
        Assert.False(first.IsReplay);
        Assert.True(replay.Accepted);
        Assert.True(replay.IsReplay);
        Assert.False(rebound.Accepted);
        Assert.Equal("update_receipt_binding_conflict", rebound.Code);
    }

    [Fact]
    public void StagedReceiptSurvivesRestartAndPreventsSecondActivation()
    {
        const string commandId = "33333333-3333-4333-8333-333333333333";
        const string nonce = "44444444-4444-4444-8444-444444444444";
        using (var first = new AgentStateDb(_path))
        {
            first.RegisterUpdateCommandReceipt(commandId, nonce, "hash", "3.15.8");
            first.MarkUpdateCommandReceipt(commandId, "staged");
        }

        using var restarted = new AgentStateDb(_path);
        var replay = restarted.RegisterUpdateCommandReceipt(
            commandId, nonce, "hash", "3.15.8");
        Assert.True(replay.Accepted);
        Assert.True(replay.IsReplay);
        Assert.Equal("staged", replay.State);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
        try { File.Delete(_path + "-wal"); } catch { }
        try { File.Delete(_path + "-shm"); } catch { }
    }
}
