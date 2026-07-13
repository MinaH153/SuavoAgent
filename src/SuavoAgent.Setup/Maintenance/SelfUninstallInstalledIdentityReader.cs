using System.Text.Json;
using Microsoft.Win32;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Setup.Maintenance;

internal static partial class SelfUninstallCompletionFinalizer
{
    private static SelfUninstallInstalledIdentity? ReadInstalledIdentity(
        string installDirectory,
        string dataDirectory,
        Func<string?> machineFingerprint)
    {
        try
        {
            var install = new DirectoryInfo(Path.GetFullPath(installDirectory));
            var data = new DirectoryInfo(Path.GetFullPath(dataDirectory));
            if (!install.Exists || install.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                !data.Exists || data.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return null;
            var appSettings = new FileInfo(Path.Combine(
                install.FullName,
                "appsettings.json"));
            if (!appSettings.Exists || appSettings.Length is <= 0 or > MaxAppSettingsBytes ||
                appSettings.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return null;
            using var document = JsonDocument.Parse(ReadBoundedFile(
                appSettings.FullName,
                MaxAppSettingsBytes));
            if (!document.RootElement.TryGetProperty("Agent", out var agent) ||
                agent.ValueKind != JsonValueKind.Object)
                return null;
            var configuredAgent = ReadString(agent, "AgentId");
            var configuredPharmacy = ReadString(agent, "PharmacyId");
            var cloudUrl = ReadString(agent, "CloudUrl");
            var maintenanceKeyId = ReadString(agent, "MaintenanceAttestationKeyId");
            var fingerprint = machineFingerprint();
            if (new[] { configuredAgent, configuredPharmacy, cloudUrl, fingerprint, maintenanceKeyId }
                .Any(string.IsNullOrWhiteSpace) ||
                !TryValidateCloudOrigin(cloudUrl!, out var cloudOrigin))
                return null;

            var store = new DpapiCredentialStore(
                InitialCredentialPersister.CredentialPath(dataDirectory));
            var protectedAgent = store.Get(CredentialKeys.AgentId);
            var protectedPharmacy = store.Get(CredentialKeys.PharmacyId);
            var deviceKeyId = store.Get(CredentialKeys.DeviceKeyId);
            if (!string.Equals(configuredAgent, protectedAgent, StringComparison.Ordinal) ||
                !string.Equals(configuredPharmacy, protectedPharmacy, StringComparison.Ordinal) ||
                deviceKeyId is not { Length: 64 } ||
                maintenanceKeyId is not { Length: 64 } ||
                deviceKeyId.Any(character =>
                    character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                maintenanceKeyId.Any(character =>
                    character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                return null;
            return new(
                configuredAgent!,
                configuredPharmacy!,
                fingerprint!,
                deviceKeyId,
                maintenanceKeyId!,
                cloudOrigin!);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryValidateCloudOrigin(string value, out Uri? origin)
    {
        origin = null;
        if (!Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out var candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(candidate.UserInfo) ||
            !string.IsNullOrEmpty(candidate.Query) ||
            !string.IsNullOrEmpty(candidate.Fragment) ||
            candidate.AbsolutePath is not ("" or "/") ||
            !candidate.IsDefaultPort ||
            !string.Equals(candidate.Host, "suavollc.com", StringComparison.OrdinalIgnoreCase))
            return false;
        origin = new Uri(candidate.GetLeftPart(UriPartial.Authority), UriKind.Absolute);
        return true;
    }

    private static string? ReadAuthoritativeMachineFingerprint()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography",
                writable: false);
            return key?.GetValue("MachineGuid") as string;
        }
        catch { return null; }
    }
}
