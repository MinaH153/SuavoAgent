namespace SuavoAgent.Core.Config;

/// <summary>Stable names in the machine-protected credential store.</summary>
public static class CredentialKeys
{
    public const string AuthKey = "AuthKey";
    public const string AgentId = "AgentId";
    public const string PharmacyId = "PharmacyId";
    public const string DeviceKeyName = "DeviceKeyName";
    public const string DeviceKeyId = "DeviceKeyId";

    // Two-phase native install provisioning. Setup stages these without
    // replacing the last-known-good identity. Only the matching target cohort
    // may consume them; Setup promotes them after the full health milestone.
    public const string PendingAuthKey = "PendingAuthKey";
    public const string PendingAgentId = "PendingAgentId";
    public const string PendingPharmacyId = "PendingPharmacyId";
    public const string PendingVersion = "PendingVersion";
    public const string PendingCloudUrl = "PendingCloudUrl";
    public const string PendingProvisioningId = "PendingProvisioningId";
    public const string PendingDeviceCode = "PendingDeviceCode";
    public const string PendingDeviceFingerprint = "PendingDeviceFingerprint";
    public const string PendingDeviceKeyName = "PendingDeviceKeyName";
    public const string PendingDeviceKeyId = "PendingDeviceKeyId";
    public const string PendingDeviceChallenge = "PendingDeviceChallenge";

    // Set in the same atomic update as a legacy appsettings migration. It is
    // deleted only after the chained state-db audit record has been appended.
    public const string AuthKeyMigrationAuditPending = "AuthKeyMigrationAuditPending";
}
