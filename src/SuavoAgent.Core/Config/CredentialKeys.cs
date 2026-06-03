namespace SuavoAgent.Core.Config;

/// <summary>
/// Canonical keys for <see cref="IEncryptedCredentialStore"/> entries written
/// by the device-code onboarding flow. Typed constants so slice C2 (boot
/// wiring) and the Setup flow agree on names rather than passing string
/// literals around.
/// </summary>
public static class CredentialKeys
{
    public const string AuthKey = "AuthKey";
    public const string AgentId = "AgentId";
    public const string PharmacyId = "PharmacyId";
}
