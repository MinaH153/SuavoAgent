using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Issues the target-only health challenge after the prior cohort is stopped,
/// retains an authoritative SYSTEM copy, and requires the new Core to publish a
/// matching milestone only after a successful cloud heartbeat.
/// </summary>
internal sealed class OtaActivationHealthCoordinator
{
    private readonly string _updateRoot;
    private readonly string _systemClaimDirectory;

    public OtaActivationHealthCoordinator(string updateRoot, string systemClaimDirectory)
    {
        _updateRoot = Path.GetFullPath(updateRoot);
        _systemClaimDirectory = Path.GetFullPath(systemClaimDirectory);
    }

    public UpdateActivationHealthChallenge Issue(
        UpdateActivationClaimPointer pointer,
        InstalledUpdateIdentity identity,
        DateTimeOffset now)
    {
        var challenge = UpdateActivationContract.CreateHealthChallenge(
            pointer,
            identity.AgentId,
            identity.MachineFingerprint,
            now);
        if (!UpdateActivationContract.ValidateHealthChallenge(challenge, now, out var code))
            throw new InvalidDataException("Activation health challenge rejected: " + code);
        Directory.CreateDirectory(_systemClaimDirectory);
        Directory.CreateDirectory(_updateRoot);
        var systemPath = Path.Combine(
            _systemClaimDirectory,
            UpdateActivationContract.HealthChallengeFileName);
        var runtimePath = UpdateActivationContract.DefaultHealthChallengePath(_updateRoot);
        var milestonePath = UpdateActivationContract.DefaultHealthMilestonePath(_updateRoot);
        TryDelete(milestonePath);
        var serialized = UpdateActivationContract.Serialize(challenge);
        WriteAtomic(systemPath, serialized);
        WriteAtomic(runtimePath, serialized);
        return challenge;
    }

    public async Task<VerifyOutcome> WaitAsync(
        UpdateActivationHealthChallenge challenge,
        string installDirectory,
        string dataDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Action? progress = null)
    {
        var started = DateTimeOffset.UtcNow;
        progress?.Invoke();
        var local = await NativeInstallHealthMilestone.WaitAsync(
            installDirectory,
            dataDirectory,
            timeout,
            cancellationToken);
        if (!local.Passed) return local;
        progress?.Invoke();

        var deadline = started + timeout;
        var milestonePath = UpdateActivationContract.DefaultHealthMilestonePath(_updateRoot);
        if (TryReadMilestone(milestonePath, out var immediate) &&
            UpdateActivationContract.ValidateHealthMilestone(
                immediate!,
                challenge,
                DateTimeOffset.UtcNow,
                out _))
        {
            PersistMilestone(immediate!);
            return local;
        }
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke();
            if (TryReadMilestone(milestonePath, out var milestone) &&
                UpdateActivationContract.ValidateHealthMilestone(
                    milestone!,
                    challenge,
                    DateTimeOffset.UtcNow,
                    out _))
            {
                PersistMilestone(milestone!);
                return local;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        var gates = local.Gates.ToList();
        gates.Add(new GateResult(
            "Cloud activation",
            GateState.Fail,
            "The target cohort did not publish its challenge-bound successful cloud heartbeat before timeout."));
        return new VerifyOutcome(false, gates, "Target cloud-heartbeat milestone timed out.");
    }

    public void CleanupRuntimeProofs()
    {
        TryDelete(UpdateActivationContract.DefaultHealthChallengePath(_updateRoot));
        TryDelete(UpdateActivationContract.DefaultHealthMilestonePath(_updateRoot));
    }

    public bool HasDurableMilestone(
        UpdateActivationClaimPointer pointer,
        InstalledUpdateIdentity identity,
        DateTimeOffset now)
    {
        try
        {
            var challengePath = Path.Combine(
                _systemClaimDirectory,
                UpdateActivationContract.HealthChallengeFileName);
            var challengeJson = BoundedFile.ReadUtf8(
                challengePath,
                UpdateActivationContract.MaxHealthProofBytes);
            if (!UpdateActivationContract.TryDeserializeHealthChallenge(
                    challengeJson,
                    out var challenge,
                    out _) ||
                !DateTimeOffset.TryParse(challenge!.IssuedAtUtc, out var issuedAt) ||
                !UpdateActivationContract.ValidateHealthChallenge(challenge, issuedAt, out _) ||
                !string.Equals(challenge!.ReplayId, pointer.ReplayId, StringComparison.Ordinal) ||
                !string.Equals(challenge.StagingId, pointer.StagingId, StringComparison.Ordinal) ||
                !UpdateActivationContract.VersionsEquivalent(
                    challenge.TargetVersion,
                    pointer.TargetVersion) ||
                !string.Equals(challenge.AgentId, identity.AgentId, StringComparison.Ordinal) ||
                !string.Equals(
                    challenge.MachineFingerprint,
                    identity.MachineFingerprint,
                    StringComparison.Ordinal))
                return false;
            var milestonePath = Path.Combine(
                _systemClaimDirectory,
                UpdateActivationContract.HealthMilestoneFileName);
            if (!TryReadMilestone(milestonePath, out var milestone)) return false;
            if (!DateTimeOffset.TryParse(milestone!.CloudHeartbeatAtUtc, out var heartbeatAt))
                return false;
            return UpdateActivationContract.ValidateHealthMilestone(
                milestone,
                challenge,
                heartbeatAt,
                out _);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadMilestone(
        string path,
        out UpdateActivationHealthMilestone? milestone)
    {
        milestone = null;
        try
        {
            if (!File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return false;
            var json = BoundedFile.ReadUtf8(
                path,
                UpdateActivationContract.MaxHealthProofBytes);
            return UpdateActivationContract.TryDeserializeHealthMilestone(
                json,
                out milestone,
                out _);
        }
        catch
        {
            return false;
        }
    }

    private void PersistMilestone(UpdateActivationHealthMilestone milestone)
    {
        var path = Path.Combine(
            _systemClaimDirectory,
            UpdateActivationContract.HealthMilestoneFileName);
        WriteAtomic(path, UpdateActivationContract.Serialize(milestone));
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
