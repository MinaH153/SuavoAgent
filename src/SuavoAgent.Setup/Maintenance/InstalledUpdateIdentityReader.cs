using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record InstalledUpdateIdentity(
    string AgentId,
    string MachineFingerprint,
    string Version);

internal static class InstalledUpdateIdentityReader
{
    private const int MaxSettingsBytes = 1024 * 1024;
    private const int MaxInstallStateBytes = 64 * 1024;

    public static InstalledUpdateIdentity? TryRead(string installDirectory)
    {
        try
        {
            var settingsPath = Path.Combine(installDirectory, "appsettings.json");
            var statePath = Path.Combine(installDirectory, MaintenanceContract.InstallStateFileName);
            using var settings = JsonDocument.Parse(
                BoundedFile.ReadBytes(settingsPath, MaxSettingsBytes));
            using var state = JsonDocument.Parse(
                BoundedFile.ReadBytes(statePath, MaxInstallStateBytes));
            if (!settings.RootElement.TryGetProperty("Agent", out var agent) ||
                agent.ValueKind != JsonValueKind.Object)
                return null;
            var agentId = ReadString(agent, "AgentId");
            var fingerprint = ReadString(agent, "MachineFingerprint");
            var configuredVersion = NormalizeVersion(ReadString(agent, "Version"));
            var installedVersion = NormalizeVersion(ReadString(state.RootElement, "version"));
            if (!IsSafeToken(agentId, 160) ||
                !IsSafeToken(fingerprint, 256) ||
                !IsSafeToken(configuredVersion, 80) ||
                !string.Equals(configuredVersion, installedVersion, StringComparison.OrdinalIgnoreCase))
                return null;
            return new InstalledUpdateIdentity(agentId!, fingerprint!, installedVersion!);
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            JsonException or
            ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? NormalizeVersion(string? value) => value?.Trim().TrimStart('v');

    private static bool IsSafeToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' or ':');
}
