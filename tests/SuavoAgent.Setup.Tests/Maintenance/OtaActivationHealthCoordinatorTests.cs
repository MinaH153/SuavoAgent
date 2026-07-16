using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class OtaActivationHealthCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 10, 20, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-ota-health-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Durable_system_milestone_remains_valid_for_crash_recovery_after_runtime_ttl()
    {
        var updateRoot = Path.Combine(_root, "updates");
        var claimDirectory = Path.Combine(_root, "maintenance", "coordinator", new string('b', 64));
        var coordinator = new OtaActivationHealthCoordinator(updateRoot, claimDirectory);
        var pointer = Pointer(claimDirectory);
        var identity = new InstalledUpdateIdentity("agent-1", "fingerprint-1", "2.0.0");
        var challenge = coordinator.Issue(pointer, identity, Now);
        var milestone = Milestone(challenge, Now.AddSeconds(5));

        Directory.CreateDirectory(claimDirectory);
        File.WriteAllText(
            Path.Combine(claimDirectory, UpdateActivationContract.HealthMilestoneFileName),
            UpdateActivationContract.Serialize(milestone),
            new UTF8Encoding(false));

        Assert.True(coordinator.HasDurableMilestone(
            pointer,
            identity,
            Now + UpdateActivationContract.MaximumHealthMilestoneAge + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Durable_milestone_is_bound_to_exact_claim_identity_and_nonce()
    {
        var updateRoot = Path.Combine(_root, "updates");
        var claimDirectory = Path.Combine(_root, "maintenance", "coordinator", new string('b', 64));
        var coordinator = new OtaActivationHealthCoordinator(updateRoot, claimDirectory);
        var pointer = Pointer(claimDirectory);
        var identity = new InstalledUpdateIdentity("agent-1", "fingerprint-1", "2.0.0");
        var challenge = coordinator.Issue(pointer, identity, Now);
        var forged = Milestone(challenge, Now.AddSeconds(5)) with
        {
            ChallengeNonce = new string('c', 64),
        };
        File.WriteAllText(
            Path.Combine(claimDirectory, UpdateActivationContract.HealthMilestoneFileName),
            UpdateActivationContract.Serialize(forged),
            new UTF8Encoding(false));

        Assert.False(coordinator.HasDurableMilestone(pointer, identity, Now.AddSeconds(10)));
        Assert.False(coordinator.HasDurableMilestone(
            pointer,
            identity with { AgentId = "other-agent" },
            Now.AddSeconds(10)));
    }

    private static UpdateActivationClaimPointer Pointer(string claimDirectory) => new(
        UpdateActivationContract.SchemaVersion,
        new string('a', 64),
        new string('b', 64),
        "2.0.0",
        Path.Combine(claimDirectory, UpdateActivationContract.ActivationRequestFileName),
        Path.Combine(claimDirectory, "payload"),
        Now.ToString("O"),
        Now.ToString("O"));

    private static UpdateActivationHealthMilestone Milestone(
        UpdateActivationHealthChallenge challenge,
        DateTimeOffset heartbeatAt) => new(
        UpdateActivationContract.SchemaVersion,
        challenge.ReplayId,
        challenge.StagingId,
        challenge.TargetVersion,
        challenge.ChallengeNonce,
        challenge.AgentId,
        challenge.MachineFingerprint,
        challenge.TargetVersion,
        heartbeatAt.ToString("O"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { }
    }
}
