namespace SuavoAgent.Core.Config;

public static class CredentialStoreFactory
{
    public static IEncryptedCredentialStore Create()
    {
        if (OperatingSystem.IsWindows())
            return new DpapiCredentialStore();

        throw new PlatformNotSupportedException(
            "Encrypted credential storage is only implemented for Windows (DPAPI) in this release.");
    }
}
