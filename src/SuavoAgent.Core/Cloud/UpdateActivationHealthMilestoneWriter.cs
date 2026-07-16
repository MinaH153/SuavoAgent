using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Writes the target-version proof only after the cloud heartbeat returned successfully. The
/// SYSTEM coordinator generated the one-time challenge after quiescing the prior cohort and verifies
/// the exact milestone before deleting rollback state. No heartbeat payload or PHI is persisted.
/// </summary>
internal static class UpdateActivationHealthMilestoneWriter
{
    public static bool TryWriteAfterSuccessfulHeartbeat(
        string runningVersion,
        string? agentId,
        string? machineFingerprint,
        ILogger logger,
        DateTimeOffset? nowOverride = null,
        string? updateRoot = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var root = updateRoot ?? UpdateActivationContract.DefaultUpdateRoot();
        var challengePath = UpdateActivationContract.DefaultHealthChallengePath(root);
        var milestonePath = UpdateActivationContract.DefaultHealthMilestonePath(root);
        if (!File.Exists(challengePath)) return false;

        try
        {
            var challengeBytes = ReadBounded(
                challengePath,
                UpdateActivationContract.MaxHealthProofBytes);
            var challengeJson = new UTF8Encoding(false, true).GetString(challengeBytes);
            if (!UpdateActivationContract.TryDeserializeHealthChallenge(
                    challengeJson,
                    out var challenge,
                    out var deserializeCode))
            {
                logger.LogWarning(
                    "Update health challenge rejected: {Code}",
                    deserializeCode);
                return false;
            }
            if (!UpdateActivationContract.ValidateHealthChallenge(
                    challenge!,
                    now,
                    out var validationCode))
            {
                logger.LogWarning(
                    "Update health challenge rejected: {Code}",
                    validationCode);
                return false;
            }
            if (!string.Equals(challenge!.AgentId, agentId, StringComparison.Ordinal) ||
                !string.Equals(
                    challenge.MachineFingerprint,
                    machineFingerprint,
                    StringComparison.Ordinal) ||
                !UpdateActivationContract.VersionsEquivalent(
                    challenge.TargetVersion,
                    runningVersion))
            {
                logger.LogWarning(
                    "Update health challenge does not match this target process identity/version");
                return false;
            }

            var milestone = new UpdateActivationHealthMilestone(
                UpdateActivationContract.SchemaVersion,
                challenge.ReplayId,
                challenge.StagingId,
                challenge.TargetVersion,
                challenge.ChallengeNonce,
                challenge.AgentId,
                challenge.MachineFingerprint,
                runningVersion,
                now.ToString("O"));
            if (!UpdateActivationContract.ValidateHealthMilestone(
                    milestone,
                    challenge,
                    now,
                    out var milestoneCode))
            {
                logger.LogWarning(
                    "Update health milestone refused before write: {Code}",
                    milestoneCode);
                return false;
            }

            // The SYSTEM challenge must remain byte-identical through validation and publication.
            var secondChallenge = ReadBounded(
                challengePath,
                UpdateActivationContract.MaxHealthProofBytes);
            if (!CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(challengeBytes),
                    SHA256.HashData(secondChallenge)))
            {
                logger.LogWarning("Update health challenge changed during milestone publication");
                return false;
            }

            Directory.CreateDirectory(root);
            var temporaryPath = milestonePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(
                    temporaryPath,
                    UpdateActivationContract.Serialize(milestone),
                    new UTF8Encoding(false));
                File.Move(temporaryPath, milestonePath, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
            logger.LogInformation(
                "Published target-version cloud-heartbeat milestone for SYSTEM update commitment");
            return true;
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            DecoderFallbackException)
        {
            logger.LogSafeWarning(ex);
            return false;
        }
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Update health proof cannot be a reparse point");
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Update health proof has an invalid size");
        var bytes = new byte[maximumBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total <= 0 || total > maximumBytes || stream.ReadByte() != -1)
            throw new InvalidDataException("Update health proof has an invalid size");
        return bytes.AsSpan(0, total).ToArray();
    }
}
