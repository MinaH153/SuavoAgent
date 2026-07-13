using System.Security.Cryptography;
using System.Text.Json;

namespace SuavoAgent.Contracts.Maintenance;

public sealed record UpdateActivationHealthChallenge(
    int SchemaVersion,
    string ReplayId,
    string StagingId,
    string TargetVersion,
    string ChallengeNonce,
    string AgentId,
    string MachineFingerprint,
    string IssuedAtUtc);

public sealed record UpdateActivationHealthMilestone(
    int SchemaVersion,
    string ReplayId,
    string StagingId,
    string TargetVersion,
    string ChallengeNonce,
    string AgentId,
    string MachineFingerprint,
    string RunningVersion,
    string CloudHeartbeatAtUtc);

public static partial class UpdateActivationContract
{
    public const string HealthChallengeFileName = "activation-health-challenge.json";
    public const string HealthMilestoneFileName = "activation-health-milestone.json";
    public const int MaxHealthProofBytes = 64 * 1024;
    public static readonly TimeSpan MaximumHealthChallengeAge = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumHealthMilestoneAge = TimeSpan.FromMinutes(5);

    public static string DefaultHealthChallengePath(string? updateRoot = null) =>
        Path.Combine(updateRoot ?? DefaultUpdateRoot(), HealthChallengeFileName);

    public static string DefaultHealthMilestonePath(string? updateRoot = null) =>
        Path.Combine(updateRoot ?? DefaultUpdateRoot(), HealthMilestoneFileName);

    public static UpdateActivationHealthChallenge CreateHealthChallenge(
        UpdateActivationClaimPointer pointer,
        string agentId,
        string machineFingerprint,
        DateTimeOffset now) =>
        new(
            SchemaVersion,
            (pointer ?? throw new ArgumentNullException(nameof(pointer))).ReplayId,
            pointer.StagingId,
            pointer.TargetVersion,
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            agentId,
            machineFingerprint,
            now.ToString("O"));

    public static string Serialize(UpdateActivationHealthChallenge challenge) =>
        JsonSerializer.Serialize(challenge, JsonOptions);

    public static string Serialize(UpdateActivationHealthMilestone milestone) =>
        JsonSerializer.Serialize(milestone, JsonOptions);

    public static bool TryDeserializeHealthChallenge(
        string json,
        out UpdateActivationHealthChallenge? challenge,
        out string rejectionCode) =>
        TryDeserializeBounded(
            json,
            MaxHealthProofBytes,
            "health_challenge",
            out challenge,
            out rejectionCode);

    public static bool TryDeserializeHealthMilestone(
        string json,
        out UpdateActivationHealthMilestone? milestone,
        out string rejectionCode) =>
        TryDeserializeBounded(
            json,
            MaxHealthProofBytes,
            "health_milestone",
            out milestone,
            out rejectionCode);

    public static bool ValidateHealthChallenge(
        UpdateActivationHealthChallenge challenge,
        DateTimeOffset now,
        out string rejectionCode)
    {
        rejectionCode = "health_challenge_invalid";
        if (challenge is null) return false;
        if (challenge.SchemaVersion != SchemaVersion)
        {
            rejectionCode = "health_challenge_schema_mismatch";
            return false;
        }
        if (!IsSha256Hex(challenge.ReplayId) ||
            !IsSha256Hex(challenge.StagingId) ||
            !IsSha256Hex(challenge.ChallengeNonce) ||
            !IsSafeToken(challenge.TargetVersion, 80) ||
            !IsSafeToken(challenge.AgentId, 160) ||
            !IsSafeToken(challenge.MachineFingerprint, 256) ||
            !TryValidateTimestamp(
                challenge.IssuedAtUtc,
                now,
                MaximumHealthChallengeAge,
                out _))
            return false;
        if (!TryNormalizeVersion(challenge.TargetVersion, out _))
        {
            rejectionCode = "health_challenge_version_invalid";
            return false;
        }
        rejectionCode = "valid";
        return true;
    }

    public static bool ValidateHealthMilestone(
        UpdateActivationHealthMilestone milestone,
        UpdateActivationHealthChallenge challenge,
        DateTimeOffset now,
        out string rejectionCode)
    {
        rejectionCode = "health_milestone_invalid";
        if (milestone is null || challenge is null) return false;
        if (milestone.SchemaVersion != SchemaVersion)
        {
            rejectionCode = "health_milestone_schema_mismatch";
            return false;
        }
        if (!string.Equals(milestone.ReplayId, challenge.ReplayId, StringComparison.Ordinal) ||
            !string.Equals(milestone.StagingId, challenge.StagingId, StringComparison.Ordinal) ||
            !string.Equals(milestone.TargetVersion, challenge.TargetVersion, StringComparison.Ordinal) ||
            !string.Equals(milestone.ChallengeNonce, challenge.ChallengeNonce, StringComparison.Ordinal) ||
            !string.Equals(milestone.AgentId, challenge.AgentId, StringComparison.Ordinal) ||
            !string.Equals(
                milestone.MachineFingerprint,
                challenge.MachineFingerprint,
                StringComparison.Ordinal))
        {
            rejectionCode = "health_milestone_challenge_mismatch";
            return false;
        }
        if (!VersionsEquivalent(milestone.RunningVersion, challenge.TargetVersion))
        {
            rejectionCode = "health_milestone_version_mismatch";
            return false;
        }
        if (!TryValidateTimestamp(
                milestone.CloudHeartbeatAtUtc,
                now,
                MaximumHealthMilestoneAge,
                out var heartbeatAt) ||
            !TryValidateTimestampWithoutMaximumAge(
                challenge.IssuedAtUtc,
                now,
                out var issuedAt) ||
            heartbeatAt < issuedAt - MaximumFutureSkew)
        {
            rejectionCode = "health_milestone_timestamp_invalid";
            return false;
        }
        rejectionCode = "valid";
        return true;
    }

    public static bool VersionsEquivalent(string? left, string? right) =>
        TryNormalizeVersion(left, out var leftVersion) &&
        TryNormalizeVersion(right, out var rightVersion) &&
        leftVersion == rightVersion;

    private static bool TryNormalizeVersion(string? value, out Version version)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('v').Split('-', 2)[0];
        return Version.TryParse(normalized, out version!);
    }
}
