using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Core.Config;

/// <summary>
/// DPAPI-encrypts plaintext ApiKey / SqlPassword in appsettings.json at first run (H-1).
/// Values are tagged with "DPAPI:" prefix so they survive config hot-reload without re-encryption.
/// Non-Windows: no-op (DPAPI unavailable).
/// </summary>
public static class CredentialProtector
{
    private const string Prefix = "DPAPI:";

    [SupportedOSPlatform("windows")]
    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(Prefix, StringComparison.Ordinal))
            return value;
        var enc = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(enc);
    }

    [SupportedOSPlatform("windows")]
    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        var dec = ProtectedData.Unprotect(
            Convert.FromBase64String(value[Prefix.Length..]), null, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(dec);
    }

    /// <summary>
    /// Rewrites appsettings.json in-place, replacing plaintext ApiKey / SqlPassword with DPAPI-wrapped values.
    /// Safe to call on every startup — already-encrypted values are skipped.
    /// </summary>
    public static void SealSecretsFile(string appSettingsPath, ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!File.Exists(appSettingsPath)) return;

        try
        {
            var json = File.ReadAllText(appSettingsPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("Agent", out var agentEl)) return;

            bool modified = false;
            var root = JsonNode.Parse(json)!.AsObject();
            var agent = root["Agent"]!.AsObject();

            foreach (var field in new[] { "ApiKey", "SqlPassword" })
            {
                if (agent[field] is JsonValue jv && jv.TryGetValue<string>(out var raw)
                    && !string.IsNullOrEmpty(raw)
                    && !raw.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    agent[field] = Protect(raw);
                    modified = true;
                }
            }

            // Also seal per-pharmacy SqlPasswords
            if (agent["Pharmacies"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is not JsonObject ph) continue;
                    if (ph["SqlPassword"] is JsonValue pv && pv.TryGetValue<string>(out var pw)
                        && !string.IsNullOrEmpty(pw)
                        && !pw.StartsWith(Prefix, StringComparison.Ordinal))
                    {
                        ph["SqlPassword"] = Protect(pw);
                        modified = true;
                    }
                }
            }

            if (!modified) return;

            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(appSettingsPath, root.ToJsonString(opts));
            logger.LogInformation("Sealed credentials in appsettings.json with DPAPI (H-1)");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to seal credentials in appsettings.json — plaintext credentials remain");
        }
    }
}
