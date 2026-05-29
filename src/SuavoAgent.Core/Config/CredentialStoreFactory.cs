namespace SuavoAgent.Core.Config;

/// <summary>
/// Selects the OS-native credential store. Phase 1 is Windows-only (DPAPI);
/// macOS Keychain is a v5 follow-up (the Mac runtime port — spec §3), so we
/// fail loudly rather than silently storing the auth key in a non-persistent
/// or insecure location on an unsupported OS.
/// </summary>
public static class CredentialStoreFactory
{
    public static IEncryptedCredentialStore Create()
    {
        if (OperatingSystem.IsWindows())
            return new DpapiCredentialStore();

        throw new PlatformNotSupportedException(
            "Encrypted credential storage is only implemented for Windows (DPAPI) in this " +
            "release. macOS Keychain support is planned for the Mac runtime port (v5).");
    }
}
