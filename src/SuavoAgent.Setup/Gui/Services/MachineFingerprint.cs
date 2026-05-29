namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// Stable per-machine fingerprint used for device-code pairing and written to
/// appsettings. Reads the Windows registry MachineGuid (same source the console
/// + GUI install paths use), falling back to the machine name when the registry
/// is unavailable (e.g. non-Windows dev/test hosts).
/// </summary>
internal static class MachineFingerprint
{
    public static string Get()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography", writable: false);
            if (key?.GetValue("MachineGuid") is string guid && !string.IsNullOrEmpty(guid))
                return guid;
        }
        catch
        {
            // Registry unavailable (non-Windows host / restricted ACL) — fall back.
        }
        return Environment.MachineName;
    }
}
