using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Watchdog;
using Xunit;

namespace SuavoAgent.Watchdog.Tests;

public sealed class UpdateClaimMonitorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 22, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "suavo-claim-monitor-" + Guid.NewGuid().ToString("N")));

    public UpdateClaimMonitorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Inspect_LiveClaimWithoutCompletion_AwaitsHeartbeat()
    {
        var fixture = CreateFixture(Now.AddSeconds(-30));

        var result = fixture.Monitor.Inspect(
            _root,
            fixture.ClaimPath,
            fixture.CompletionPath,
            Now);

        Assert.Equal(UpdateClaimState.AwaitingHeartbeat, result.State);
        Assert.Equal(fixture.Pointer.ReplayId, result.Pointer!.ReplayId);
    }

    [Fact]
    public void Inspect_ExpiredClaimWithoutCompletion_RequiresResume()
    {
        var fixture = CreateFixture(Now - UpdateClaimMonitor.ClaimHeartbeatLease);

        var result = fixture.Monitor.Inspect(
            _root,
            fixture.ClaimPath,
            fixture.CompletionPath,
            Now);

        Assert.Equal(UpdateClaimState.ResumeRequired, result.State);
        Assert.Equal("claim_heartbeat_expired", result.Code);
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("rolled_back")]
    [InlineData("rejected")]
    [InlineData("failed")]
    public void Inspect_MatchingTerminalCompletion_StopsResume(string outcome)
    {
        var fixture = CreateFixture(Now - UpdateClaimMonitor.ClaimHeartbeatLease);
        Write(fixture.CompletionPath, UpdateActivationContract.Serialize(
            new UpdateActivationCompletion(
                UpdateActivationContract.SchemaVersion,
                fixture.Pointer.ReplayId,
                fixture.Pointer.StagingId,
                fixture.Pointer.TargetVersion,
                outcome,
                Now.AddMinutes(-3).ToString("O"),
                Now.AddMinutes(-1).ToString("O"))));

        var result = fixture.Monitor.Inspect(
            _root,
            fixture.ClaimPath,
            fixture.CompletionPath,
            Now);

        Assert.Equal(UpdateClaimState.Completed, result.State);
        Assert.Equal(outcome, result.Completion!.Outcome);
    }

    [Fact]
    public void Inspect_TerminalCompletionAfterPointerRemoval_RemainsObservable()
    {
        var fixture = CreateFixture(Now.AddMinutes(-3));
        Write(fixture.CompletionPath, UpdateActivationContract.Serialize(
            new UpdateActivationCompletion(
                UpdateActivationContract.SchemaVersion,
                fixture.Pointer.ReplayId,
                fixture.Pointer.StagingId,
                fixture.Pointer.TargetVersion,
                "committed",
                Now.AddMinutes(-3).ToString("O"),
                Now.AddMinutes(-1).ToString("O"))));
        File.Delete(fixture.ClaimPath);

        var result = fixture.Monitor.Inspect(
            _root,
            fixture.ClaimPath,
            fixture.CompletionPath,
            Now);

        Assert.Equal(UpdateClaimState.Completed, result.State);
        Assert.Null(result.Pointer);
        Assert.Equal("committed", result.Completion!.Outcome);
    }

    [Fact]
    public void Inspect_MismatchedCompletion_FailsClosed()
    {
        var fixture = CreateFixture(Now.AddMinutes(-3));
        Write(fixture.CompletionPath, UpdateActivationContract.Serialize(
            new UpdateActivationCompletion(
                UpdateActivationContract.SchemaVersion,
                new string('c', 64),
                fixture.Pointer.StagingId,
                fixture.Pointer.TargetVersion,
                "committed",
                Now.AddMinutes(-3).ToString("O"),
                Now.AddMinutes(-1).ToString("O"))));

        var result = fixture.Monitor.Inspect(
            _root,
            fixture.ClaimPath,
            fixture.CompletionPath,
            Now);

        Assert.Equal(UpdateClaimState.Invalid, result.State);
        Assert.Equal("completion_claim_mismatch", result.Code);
    }

    [Fact]
    public void Inspect_PointerEscapingFixedCoordinatorPath_FailsClosed()
    {
        var fixture = CreateFixture(Now.AddSeconds(-30));
        Write(fixture.ClaimPath, UpdateActivationContract.Serialize(
            fixture.Pointer with
            {
                PayloadDirectory = Path.Combine(_root, "outside-payload"),
            }));

        var result = fixture.Monitor.Inspect(
            _root,
            fixture.ClaimPath,
            fixture.CompletionPath,
            Now);

        Assert.Equal(UpdateClaimState.Invalid, result.State);
        Assert.Equal("claim_pointer_path_mismatch", result.Code);
    }

    private Fixture CreateFixture(DateTimeOffset heartbeatAt)
    {
        var stagingId = new string('a', 64);
        var claimDirectory = UpdateActivationContract.GetCoordinatorClaimDirectory(_root, stagingId);
        var requestPath = UpdateActivationContract.GetCoordinatorRequestPath(_root, stagingId);
        var payloadDirectory = UpdateActivationContract.GetCoordinatorPayloadDirectory(_root, stagingId);
        Directory.CreateDirectory(payloadDirectory);
        Write(requestPath, "{}");
        var pointer = new UpdateActivationClaimPointer(
            UpdateActivationContract.SchemaVersion,
            new string('b', 64),
            stagingId,
            "2.0.0",
            requestPath,
            payloadDirectory,
            Now.AddMinutes(-5).ToString("O"),
            heartbeatAt.ToString("O"));
        var claimPath = Path.Combine(_root, UpdateActivationContract.ActiveClaimFileName);
        var completionPath = Path.Combine(_root, UpdateActivationContract.CompletionFileName);
        Write(claimPath, UpdateActivationContract.Serialize(pointer));
        return new Fixture(
            new UpdateClaimMonitor(),
            pointer,
            claimPath,
            completionPath);
    }

    private static void Write(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
    }

    private sealed record Fixture(
        UpdateClaimMonitor Monitor,
        UpdateActivationClaimPointer Pointer,
        string ClaimPath,
        string CompletionPath);
}
