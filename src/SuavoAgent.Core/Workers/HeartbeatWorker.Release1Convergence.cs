using System.Text.Json;
using SuavoAgent.Core.Cloud;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    private async Task<bool> HandleRelease1ConvergenceChallengeAsync(
        JsonElement signedEnvelope,
        SignedCommand command,
        CancellationToken cancellationToken)
    {
        if (_release1Convergence is null ||
            !signedEnvelope.TryGetProperty("data", out var data) ||
            !TryParseRelease1Challenge(data, command, out var challenge))
        {
            _logger.LogWarning("release1.convergence.challenge_shape_rejected");
            return false;
        }
        return await _release1Convergence.RegisterAndRetryAsync(
                challenge!,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryParseRelease1Challenge(
        JsonElement data,
        SignedCommand command,
        out Release1ConvergenceChallenge? challenge)
    {
        challenge = null;
        if (data.ValueKind != JsonValueKind.Object)
            return false;
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "commandId",
            "inventorySha256",
            "bridgeReleaseTag",
            "bridgeSourceSha",
            "expiresAt",
        };
        foreach (var property in data.EnumerateObject())
        {
            if (!expected.Remove(property.Name) ||
                property.Value.ValueKind != JsonValueKind.String)
                return false;
        }
        if (expected.Count != 0)
            return false;
        var commandId = data.GetProperty("commandId").GetString();
        var inventorySha256 = data.GetProperty("inventorySha256").GetString();
        var bridgeReleaseTag = data.GetProperty("bridgeReleaseTag").GetString();
        var bridgeSourceSha = data.GetProperty("bridgeSourceSha").GetString();
        var expiresAt = data.GetProperty("expiresAt").GetString();
        if (commandId is null || inventorySha256 is null ||
            bridgeReleaseTag is null || bridgeSourceSha is null ||
            expiresAt is null ||
            !Guid.TryParseExact(commandId, "D", out var parsedCommandId) ||
            !string.Equals(
                parsedCommandId.ToString("D"),
                commandId,
                StringComparison.Ordinal) ||
            !string.Equals(command.ExpiresAt, expiresAt, StringComparison.Ordinal))
            return false;
        challenge = new(
            commandId,
            inventorySha256,
            bridgeReleaseTag,
            bridgeSourceSha,
            expiresAt,
            command);
        return true;
    }
}
