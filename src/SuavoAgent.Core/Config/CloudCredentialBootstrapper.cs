using System.Security;

namespace SuavoAgent.Core.Config;

internal enum CloudCredentialSource
{
    Missing,
    ProtectedStore,
    PendingProvisioning,
    LegacyAppSettingsMigration,
}

internal sealed record CloudCredentialBootstrapResult(
    string? AuthKey,
    CloudCredentialSource Source,
    bool MigrationAuditPending,
    string? ProvisioningId,
    string? DeviceKeyName,
    string? DeviceKeyId,
    string? DeviceCode,
    string? DeviceChallenge,
    string? DeviceFingerprint);

/// <summary>
/// Resolves cloud authentication before any cloud client is constructed.
/// The protected ProgramData store is authoritative. A legacy appsettings key
/// is accepted only when the store is empty, migrated atomically, and never
/// written back to Program Files (which remains LocalService RX).
/// </summary>
internal static class CloudCredentialBootstrapper
{
    internal static CloudCredentialBootstrapResult LoadOrMigrate(
        IEncryptedCredentialStore store,
        AgentOptions options,
        bool unprotectLegacyValue)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);

        var storedKey = store.Get(CredentialKeys.AuthKey);
        var storedAgentId = store.Get(CredentialKeys.AgentId);
        var storedPharmacyId = store.Get(CredentialKeys.PharmacyId);
        var storedDeviceKeyName = store.Get(CredentialKeys.DeviceKeyName);
        var storedDeviceKeyId = store.Get(CredentialKeys.DeviceKeyId);
        var pending = store.Get(CredentialKeys.AuthKeyMigrationAuditPending) is not null;

        var pendingKey = store.Get(CredentialKeys.PendingAuthKey);
        var pendingAgentId = store.Get(CredentialKeys.PendingAgentId);
        var pendingPharmacyId = store.Get(CredentialKeys.PendingPharmacyId);
        var pendingVersion = store.Get(CredentialKeys.PendingVersion);
        var pendingCloudUrl = store.Get(CredentialKeys.PendingCloudUrl);
        var pendingProvisioningId = store.Get(CredentialKeys.PendingProvisioningId);
        var pendingDeviceKeyName = store.Get(CredentialKeys.PendingDeviceKeyName);
        var pendingDeviceKeyId = store.Get(CredentialKeys.PendingDeviceKeyId);
        var pendingDeviceFingerprint = store.Get(CredentialKeys.PendingDeviceFingerprint);
        var pendingDeviceCode = store.Get(CredentialKeys.PendingDeviceCode);
        var pendingDeviceChallenge = store.Get(CredentialKeys.PendingDeviceChallenge);
        var pendingParts = new[]
            { pendingKey, pendingAgentId, pendingPharmacyId, pendingVersion, pendingCloudUrl, pendingProvisioningId }
            .Count(value => value is not null);
        if (pendingParts is > 0 and < 6)
            throw new InvalidDataException("Pending cloud credential transaction is incomplete.");
        var pendingProofParts = new[]
            {
                pendingDeviceKeyName,
                pendingDeviceKeyId,
                pendingDeviceFingerprint,
                pendingDeviceCode,
                pendingDeviceChallenge,
            }
            .Count(value => value is not null);
        if (pendingParts > 0 && pendingProofParts != 5)
            throw new InvalidDataException("Pending device proof transaction is incomplete.");
        ValidateDeviceKeyPair(pendingDeviceKeyName, pendingDeviceKeyId, "Pending");
        ValidateDeviceKeyPair(storedDeviceKeyName, storedDeviceKeyId, "Protected");
        if (pendingDeviceKeyId is not null &&
            (string.IsNullOrWhiteSpace(pendingDeviceFingerprint) ||
             pendingDeviceFingerprint.Length > 256 ||
             pendingDeviceFingerprint.Any(char.IsControl)))
            throw new InvalidDataException("Pending TPM fingerprint metadata is invalid.");

        var pendingAlreadyPromotedLocally = pendingParts == 6 &&
            string.Equals(storedKey, pendingKey, StringComparison.Ordinal) &&
            string.Equals(storedAgentId, pendingAgentId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(storedPharmacyId, pendingPharmacyId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(storedDeviceKeyName, pendingDeviceKeyName, StringComparison.Ordinal) &&
            string.Equals(storedDeviceKeyId, pendingDeviceKeyId, StringComparison.Ordinal);

        // A target cohort may prove itself with its staged credential without
        // replacing the prior cohort's active binding. Version binding makes a
        // rolled-back binary ignore the pending target transaction.
        if (pendingParts == 6 &&
            !pendingAlreadyPromotedLocally &&
            string.Equals(
                NormalizeVersion(pendingVersion!),
                NormalizeVersion(options.Version),
                StringComparison.OrdinalIgnoreCase))
        {
            ValidateAuthKey(pendingKey);
            if (!Guid.TryParse(pendingProvisioningId, out _))
                throw new InvalidDataException("Pending cloud credential provisioning identity is invalid.");
            EnforceIdentityBinding(
                pendingAgentId,
                pendingPharmacyId,
                options.AgentId,
                options.PharmacyId,
                "Pending");
            return new(
                pendingKey,
                CloudCredentialSource.PendingProvisioning,
                MigrationAuditPending: false,
                ProvisioningId: pendingProvisioningId,
                DeviceKeyName: pendingDeviceKeyName,
                DeviceKeyId: pendingDeviceKeyId,
                DeviceCode: pendingDeviceCode,
                DeviceChallenge: pendingDeviceChallenge,
                DeviceFingerprint: pendingDeviceFingerprint);
        }

        if (!string.IsNullOrWhiteSpace(storedKey))
        {
            ValidateAuthKey(storedKey);
            EnforceIdentityBinding(
                storedAgentId,
                storedPharmacyId,
                options.AgentId,
                options.PharmacyId,
                "Protected");

            // Older device-code stores may predate identity metadata. Bind it
            // once without rotating or reserializing the auth key itself.
            if (string.IsNullOrWhiteSpace(storedAgentId) && !string.IsNullOrWhiteSpace(options.AgentId))
            {
                var metadata = IdentityMetadata(options);
                if (metadata.Count > 0)
                    store.SetMany(metadata);
            }

            return new(
                storedKey,
                CloudCredentialSource.ProtectedStore,
                pending,
                ProvisioningId: null,
                DeviceKeyName: storedDeviceKeyName,
                DeviceKeyId: storedDeviceKeyId,
                DeviceCode: null,
                DeviceChallenge: null,
                DeviceFingerprint: null);
        }

        if (pending)
            throw new InvalidDataException("Credential migration marker exists without an authentication key.");

        var legacyKey = options.ApiKey;
        if (string.IsNullOrWhiteSpace(legacyKey))
            return new(
                null,
                CloudCredentialSource.Missing,
                MigrationAuditPending: false,
                ProvisioningId: null,
                DeviceKeyName: null,
                DeviceKeyId: null,
                DeviceCode: null,
                DeviceChallenge: null,
                DeviceFingerprint: null);

        if (unprotectLegacyValue)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Legacy DPAPI credential migration requires Windows.");
            legacyKey = CredentialProtector.Unprotect(legacyKey);
        }
        ValidateAuthKey(legacyKey);

        var migrated = IdentityMetadata(options);
        migrated[CredentialKeys.AuthKey] = legacyKey!;
        migrated[CredentialKeys.AuthKeyMigrationAuditPending] = "legacy_appsettings_v1";
        store.SetMany(migrated);

        // Verify the durable source before allowing cloud clients to capture it.
        var verified = store.Get(CredentialKeys.AuthKey);
        if (!string.Equals(verified, legacyKey, StringComparison.Ordinal))
            throw new IOException("Credential migration could not be verified.");

        return new(
            verified,
            CloudCredentialSource.LegacyAppSettingsMigration,
            MigrationAuditPending: true,
            ProvisioningId: null,
            DeviceKeyName: null,
            DeviceKeyId: null,
            DeviceCode: null,
            DeviceChallenge: null,
            DeviceFingerprint: null);
    }

    internal static void ValidateSqlSecretsAreProtected(AgentOptions options, bool enforce)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!enforce) return;

        if (!CredentialProtector.IsProtected(options.SqlPassword))
            throw new SecurityException("Agent.SqlPassword is plaintext; refusing to start.");
        foreach (var pharmacy in options.Pharmacies)
        {
            if (!CredentialProtector.IsProtected(pharmacy.SqlPassword))
                throw new SecurityException("Agent.Pharmacies.SqlPassword is plaintext; refusing to start.");
        }
    }

    internal static void MarkMigrationAuditComplete(IEncryptedCredentialStore store) =>
        store.Delete(CredentialKeys.AuthKeyMigrationAuditPending);

    private static Dictionary<string, string> IdentityMetadata(AgentOptions options)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(options.AgentId))
            values[CredentialKeys.AgentId] = options.AgentId;
        if (!string.IsNullOrWhiteSpace(options.PharmacyId))
            values[CredentialKeys.PharmacyId] = options.PharmacyId;
        return values;
    }

    private static void EnforceIdentityBinding(
        string? storedAgentId,
        string? storedPharmacyId,
        string? configuredAgentId,
        string? configuredPharmacyId,
        string source)
    {
        if (!string.IsNullOrWhiteSpace(storedAgentId) &&
            !string.Equals(storedAgentId, configuredAgentId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"{source} cloud credential belongs to a different agent identity.");
        }
        if (!string.IsNullOrWhiteSpace(storedPharmacyId) &&
            !string.Equals(storedPharmacyId, configuredPharmacyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"{source} cloud credential belongs to a different pharmacy identity.");
        }
    }

    private static string NormalizeVersion(string value) =>
        value.Trim().TrimStart('v', 'V');

    private static void ValidateAuthKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.')))
        {
            throw new SecurityException("Cloud authentication key in local configuration is invalid.");
        }
    }

    private static void ValidateDeviceKeyPair(string? name, string? id, string source)
    {
        var present = new[] { name, id }.Count(value => value is not null);
        if (present == 0) return;
        if (present != 2 || string.IsNullOrWhiteSpace(name) || name.Length > 256 ||
            name.Any(char.IsControl) || id!.Length != 64 ||
            id.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException($"{source} TPM key metadata is invalid.");
    }
}
