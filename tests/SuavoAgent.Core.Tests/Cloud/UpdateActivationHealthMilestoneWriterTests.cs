using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed class UpdateActivationHealthMilestoneWriterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 23, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "suavo-core-health-proof-" + Guid.NewGuid().ToString("N")));

    public UpdateActivationHealthMilestoneWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void SuccessfulHeartbeat_MatchingChallenge_WritesFreshBoundMilestoneAtomically()
    {
        var challenge = WriteChallenge("v2.0.0", "agent-1", "fingerprint-1");

        var result = UpdateActivationHealthMilestoneWriter.TryWriteAfterSuccessfulHeartbeat(
            "2.0.0",
            "agent-1",
            "fingerprint-1",
            NullLogger.Instance,
            Now,
            _root);

        Assert.True(result);
        var milestonePath = UpdateActivationContract.DefaultHealthMilestonePath(_root);
        Assert.True(File.Exists(milestonePath));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root),
            path => Path.GetFileName(path).Contains(".tmp-", StringComparison.Ordinal));
        Assert.True(UpdateActivationContract.TryDeserializeHealthMilestone(
            File.ReadAllText(milestonePath),
            out var milestone,
            out var deserializeCode), deserializeCode);
        Assert.True(UpdateActivationContract.ValidateHealthMilestone(
            milestone!,
            challenge,
            Now,
            out var validationCode), validationCode);
    }

    [Theory]
    [InlineData("1.9.0", "agent-1", "fingerprint-1")]
    [InlineData("2.0.0", "wrong-agent", "fingerprint-1")]
    [InlineData("2.0.0", "agent-1", "wrong-fingerprint")]
    public void SuccessfulHeartbeat_WrongTargetIdentityOrVersion_WritesNothing(
        string runningVersion,
        string agentId,
        string fingerprint)
    {
        WriteChallenge("2.0.0", "agent-1", "fingerprint-1");

        Assert.False(UpdateActivationHealthMilestoneWriter.TryWriteAfterSuccessfulHeartbeat(
            runningVersion,
            agentId,
            fingerprint,
            NullLogger.Instance,
            Now,
            _root));
        Assert.False(File.Exists(UpdateActivationContract.DefaultHealthMilestonePath(_root)));
    }

    [Fact]
    public void SuccessfulHeartbeat_StaleChallenge_WritesNothing()
    {
        WriteChallenge("2.0.0", "agent-1", "fingerprint-1", Now.AddMinutes(-11));

        Assert.False(UpdateActivationHealthMilestoneWriter.TryWriteAfterSuccessfulHeartbeat(
            "2.0.0",
            "agent-1",
            "fingerprint-1",
            NullLogger.Instance,
            Now,
            _root));
    }

    private UpdateActivationHealthChallenge WriteChallenge(
        string targetVersion,
        string agentId,
        string fingerprint,
        DateTimeOffset? issuedAt = null)
    {
        var pointer = new UpdateActivationClaimPointer(
            UpdateActivationContract.SchemaVersion,
            new string('a', 64),
            new string('b', 64),
            targetVersion,
            UpdateActivationContract.GetCoordinatorRequestPath(
                Path.Combine(_root, "maintenance"),
                new string('b', 64)),
            UpdateActivationContract.GetCoordinatorPayloadDirectory(
                Path.Combine(_root, "maintenance"),
                new string('b', 64)),
            Now.ToString("O"),
            Now.ToString("O"));
        var challenge = UpdateActivationContract.CreateHealthChallenge(
            pointer,
            agentId,
            fingerprint,
            issuedAt ?? Now);
        File.WriteAllText(
            UpdateActivationContract.DefaultHealthChallengePath(_root),
            UpdateActivationContract.Serialize(challenge),
            new UTF8Encoding(false));
        return challenge;
    }
}
