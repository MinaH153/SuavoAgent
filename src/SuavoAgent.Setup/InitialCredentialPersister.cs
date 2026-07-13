using System.Security;
using System.Security.Cryptography;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Config;
using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup;

/// <summary>
/// Two-phase cloud credential provisioning. Setup stages a target-bound DPAPI
/// credential without replacing the last-known-good identity. The matching new
/// Core may use that pending credential during probation; Setup promotes it only
/// after the complete local + cloud health milestone succeeds.
/// </summary>
internal static class InitialCredentialPersister
{
    internal static string CredentialPath(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(Path.GetFullPath(dataDirectory), "credentials.dat");
    }

    internal static string Stage(string dataDirectory, SetupConfig config) =>
        Stage(CreateStore(dataDirectory), config);

    internal static void Commit(string dataDirectory, SetupConfig config) =>
        Commit(CreateStore(dataDirectory), config);

    internal static void Complete(string dataDirectory, SetupConfig config) =>
        Complete(CreateStore(dataDirectory), config);

    internal static void Abort(string dataDirectory, SetupConfig config) =>
        Abort(CreateStore(dataDirectory), config);

    internal static string Stage(IEncryptedCredentialStore store, SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(store);
        ValidateConfig(config);
        EnforceCurrentBinding(store, config);
        EnforcePendingBinding(store, config);

        var version = NormalizeVersion(config.ReleaseTag);
        var provisioningId = Guid.NewGuid().ToString("D");
        var pending = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CredentialKeys.PendingAuthKey] = config.ApiKey,
            [CredentialKeys.PendingAgentId] = config.AgentId,
            [CredentialKeys.PendingPharmacyId] = config.PharmacyId,
            [CredentialKeys.PendingVersion] = version,
            [CredentialKeys.PendingCloudUrl] = config.CloudUrl,
            [CredentialKeys.PendingProvisioningId] = provisioningId,
        };
        if (!string.IsNullOrWhiteSpace(config.DeviceCode))
            pending[CredentialKeys.PendingDeviceCode] = config.DeviceCode;
        if (!string.IsNullOrWhiteSpace(config.DeviceKeyId) &&
            !string.IsNullOrWhiteSpace(config.DeviceKeyName))
        {
            pending[CredentialKeys.PendingDeviceKeyId] = config.DeviceKeyId;
            pending[CredentialKeys.PendingDeviceKeyName] = config.DeviceKeyName;
            pending[CredentialKeys.PendingDeviceFingerprint] = config.DeviceFingerprint!;
            pending[CredentialKeys.PendingDeviceChallenge] = config.DeviceChallenge!;
        }
        store.SetMany(pending);

        VerifyPending(store, config, version, provisioningId);
        return provisioningId;
    }

    internal static void Commit(IEncryptedCredentialStore store, SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(store);
        ValidateConfig(config);
        var version = NormalizeVersion(config.ReleaseTag);
        var provisioningId = store.Get(CredentialKeys.PendingProvisioningId)
            ?? throw new InvalidDataException("Pending cloud credential has no provisioning identity.");
        VerifyPending(store, config, version, provisioningId);

        PromotePending(store);

        if (!string.Equals(store.Get(CredentialKeys.AuthKey), config.ApiKey, StringComparison.Ordinal) ||
            !string.Equals(store.Get(CredentialKeys.AgentId), config.AgentId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(store.Get(CredentialKeys.PharmacyId), config.PharmacyId, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(config.DeviceKeyId) &&
             (!string.Equals(store.Get(CredentialKeys.DeviceKeyId), config.DeviceKeyId, StringComparison.Ordinal) ||
              !string.Equals(store.Get(CredentialKeys.DeviceKeyName), config.DeviceKeyName, StringComparison.Ordinal))))
        {
            throw new IOException("Protected cloud credential promotion could not be verified.");
        }

        // Keep the pending journal until the CNG pointer switch succeeds. A
        // crash after cloud promotion is recovered forward, never by restoring
        // the now-revoked predecessor.
    }

    internal static void Complete(IEncryptedCredentialStore store, SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(store);
        ValidateConfig(config);
        if (!string.Equals(store.Get(CredentialKeys.AuthKey), config.ApiKey, StringComparison.Ordinal) ||
            !string.Equals(store.Get(CredentialKeys.AgentId), config.AgentId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(store.Get(CredentialKeys.PharmacyId), config.PharmacyId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(store.Get(CredentialKeys.DeviceKeyName), config.DeviceKeyName, StringComparison.Ordinal) ||
            !string.Equals(store.Get(CredentialKeys.DeviceKeyId), config.DeviceKeyId, StringComparison.Ordinal))
            throw new IOException("Active authority metadata is not fully promoted.");
        DeletePending(store);
    }

    internal static void Abort(IEncryptedCredentialStore store, SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(store);
        ValidateConfig(config);

        var pendingAgent = store.Get(CredentialKeys.PendingAgentId);
        var pendingPharmacy = store.Get(CredentialKeys.PendingPharmacyId);
        if (pendingAgent is null && pendingPharmacy is null)
            return;
        if (!string.Equals(pendingAgent, config.AgentId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pendingPharmacy, config.PharmacyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                "Refusing to remove a pending credential owned by a different workstation binding.");
        }
        DeletePending(store);
    }

    internal static string ProtectSqlPassword(string password)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SQL credential sealing requires Windows DPAPI.");
        return CredentialProtector.Protect(password)
               ?? throw new SecurityException("SQL credential sealing returned no protected value.");
    }

    private static IEncryptedCredentialStore CreateStore(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("SuavoAgent credential provisioning requires Windows DPAPI.");
        return new DpapiCredentialStore(CredentialPath(dataDirectory));
    }

    private static void ValidateConfig(SetupConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.ApiKey))
            throw new SecurityException("Connected installation is missing its cloud authentication key.");
        if (!Guid.TryParse(config.AgentId, out _))
            throw new SecurityException("Connected installation is missing its cloud agent identity.");
        if (string.IsNullOrWhiteSpace(config.PharmacyId) || config.PharmacyId.Length > 128)
            throw new SecurityException("Connected installation is missing its pharmacy identity.");
        var keyParts = new[] { config.DeviceKeyId, config.DeviceKeyName }
            .Count(value => !string.IsNullOrWhiteSpace(value));
        if (keyParts == 1)
            throw new SecurityException("Connected installation has incomplete TPM key metadata.");
        if (keyParts == 2 &&
            (config.DeviceKeyId!.Length != 64 ||
             config.DeviceKeyId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
             config.DeviceKeyName!.Length > 256 ||
             config.DeviceKeyName.Any(char.IsControl)))
            throw new SecurityException("Connected installation has invalid TPM key metadata.");
        if (keyParts == 2 &&
            (string.IsNullOrWhiteSpace(config.DeviceFingerprint) ||
             config.DeviceFingerprint.Length > 256 ||
             config.DeviceFingerprint.Any(char.IsControl)))
            throw new SecurityException("Connected installation has invalid TPM fingerprint metadata.");
        if (keyParts == 2 &&
            (string.IsNullOrWhiteSpace(config.DeviceChallenge) ||
             config.DeviceChallenge.Length != 43 ||
             config.DeviceChallenge.Any(c =>
                 c is not (>= 'A' and <= 'Z') and
                 not (>= 'a' and <= 'z') and
                 not (>= '0' and <= '9') and
                 not '-' and not '_')))
            throw new SecurityException("Connected installation has invalid device proof challenge metadata.");
        if (config.DeviceCode is { Length: > 128 } || config.DeviceCode?.Any(char.IsControl) == true)
            throw new SecurityException("Connected installation has an invalid device-code identity.");
        _ = NormalizeVersion(config.ReleaseTag);
    }

    private static void EnforceCurrentBinding(IEncryptedCredentialStore store, SetupConfig config)
    {
        var existingKey = store.Get(CredentialKeys.AuthKey);
        var existingAgent = store.Get(CredentialKeys.AgentId);
        var existingPharmacy = store.Get(CredentialKeys.PharmacyId);
        if (!string.IsNullOrWhiteSpace(existingKey) && string.IsNullOrWhiteSpace(existingAgent))
        {
            throw new SecurityException(
                "The protected credential has no verifiable agent identity; explicit decommission is required before reinstall.");
        }
        if (!string.IsNullOrWhiteSpace(existingAgent) &&
            !string.Equals(existingAgent, config.AgentId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                "This workstation is already bound to a different SuavoAgent identity; explicit decommission is required before reassignment.");
        }
        if (!string.IsNullOrWhiteSpace(existingPharmacy) &&
            !string.Equals(existingPharmacy, config.PharmacyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                "This workstation is already bound to a different pharmacy; explicit decommission is required before reassignment.");
        }
    }

    private static void EnforcePendingBinding(IEncryptedCredentialStore store, SetupConfig config)
    {
        var key = store.Get(CredentialKeys.PendingAuthKey);
        var agent = store.Get(CredentialKeys.PendingAgentId);
        var pharmacy = store.Get(CredentialKeys.PendingPharmacyId);
        var version = store.Get(CredentialKeys.PendingVersion);
        var cloudUrl = store.Get(CredentialKeys.PendingCloudUrl);
        var provisioningId = store.Get(CredentialKeys.PendingProvisioningId);
        var deviceKeyName = store.Get(CredentialKeys.PendingDeviceKeyName);
        var deviceKeyId = store.Get(CredentialKeys.PendingDeviceKeyId);
        var deviceFingerprint = store.Get(CredentialKeys.PendingDeviceFingerprint);
        var deviceChallenge = store.Get(CredentialKeys.PendingDeviceChallenge);
        var deviceCode = store.Get(CredentialKeys.PendingDeviceCode);
        var present = new[] { key, agent, pharmacy, version, cloudUrl, provisioningId }
            .Count(value => value is not null);
        var deviceParts = new[]
            { deviceKeyName, deviceKeyId, deviceFingerprint, deviceChallenge, deviceCode }
            .Count(value => value is not null);
        if (present is > 0 and < 6 || deviceParts is > 0 and < 5)
            throw new InvalidDataException("Pending cloud credential transaction is incomplete.");
        if (present == 0)
            return;
        if (!string.Equals(agent, config.AgentId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(pharmacy, config.PharmacyId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException(
                "A pending credential transaction belongs to a different workstation binding.");
        }
    }

    private static void VerifyPending(
        IEncryptedCredentialStore store,
        SetupConfig config,
        string version,
        string provisioningId)
    {
        if (!string.Equals(store.Get(CredentialKeys.PendingAuthKey), config.ApiKey, StringComparison.Ordinal) ||
            !string.Equals(store.Get(CredentialKeys.PendingAgentId), config.AgentId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(store.Get(CredentialKeys.PendingPharmacyId), config.PharmacyId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(store.Get(CredentialKeys.PendingVersion), version, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(store.Get(CredentialKeys.PendingCloudUrl), config.CloudUrl, StringComparison.Ordinal) ||
            !Guid.TryParse(provisioningId, out _) ||
            !string.Equals(store.Get(CredentialKeys.PendingProvisioningId), provisioningId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(store.Get(CredentialKeys.PendingDeviceCode), config.DeviceCode, StringComparison.Ordinal) ||
            !string.Equals(store.Get(CredentialKeys.PendingDeviceChallenge), config.DeviceChallenge, StringComparison.Ordinal) ||
            !string.Equals(store.Get(CredentialKeys.PendingDeviceFingerprint), config.DeviceFingerprint, StringComparison.Ordinal) ||
            !string.Equals(store.Get(CredentialKeys.PendingDeviceKeyName), config.DeviceKeyName, StringComparison.Ordinal) ||
            !string.Equals(store.Get(CredentialKeys.PendingDeviceKeyId), config.DeviceKeyId, StringComparison.Ordinal))
        {
            throw new IOException("Pending protected cloud credential write could not be verified.");
        }
    }

    private static string NormalizeVersion(string releaseTag)
    {
        var version = releaseTag?.Trim().TrimStart('v', 'V');
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || version.Any(char.IsControl))
            throw new SecurityException("Connected installation has an invalid release identity.");
        return version;
    }

    internal static bool FinalizePendingAuthority(string dataDirectory)
    {
        var store = CreateStore(dataDirectory);
        return FinalizePendingAuthority(
            store,
            DeviceAttestationKeyProvider.CreateProduction());
    }

    internal static bool FinalizePendingAuthority(
        IEncryptedCredentialStore store,
        IDeviceAttestationKeyProvider provider)
    {
        var provisioningId = store.Get(CredentialKeys.PendingProvisioningId);
        if (provisioningId is null)
            return false;
        if (!Guid.TryParseExact(provisioningId, "D", out _))
            throw new InvalidDataException("Pending provisioning identity is invalid.");
        var fingerprint = store.Get(CredentialKeys.PendingDeviceFingerprint)
            ?? throw new InvalidDataException("Pending TPM fingerprint is missing.");
        var keyId = store.Get(CredentialKeys.PendingDeviceKeyId)
            ?? throw new InvalidDataException("Pending TPM key id is missing.");
        var keyName = store.Get(CredentialKeys.PendingDeviceKeyName)
            ?? throw new InvalidDataException("Pending TPM key name is missing.");

        PromotePending(store);
        provider.CommitPending(fingerprint, keyId);
        if (!string.Equals(store.Get(CredentialKeys.DeviceKeyName), keyName, StringComparison.Ordinal) ||
            !string.Equals(store.Get(CredentialKeys.DeviceKeyId), keyId, StringComparison.Ordinal))
            throw new IOException("Recovered authority metadata does not match the pending key.");
        return true;
    }

    internal static AuthorityPromotionOutcome ReplayPendingAuthorityPromotion(
        string dataDirectory)
    {
        var store = CreateStore(dataDirectory);
        return ReplayPendingAuthorityPromotion(
            store,
            (config, provisioningId) =>
                DeviceTokenConfirmation.ConfirmAsync(
                            config,
                            provisioningId,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult());
    }

    internal static AuthorityPromotionOutcome ReplayPendingAuthorityPromotion(
        IEncryptedCredentialStore store,
        Func<SetupConfig, string, AuthorityPromotionOutcome> confirm)
    {
        var provisioningId = store.Get(CredentialKeys.PendingProvisioningId);
        if (provisioningId is null)
            return AuthorityPromotionOutcome.Unknown;
        if (!Guid.TryParseExact(provisioningId, "D", out var parsed) ||
            parsed.ToString("D") != provisioningId)
            return AuthorityPromotionOutcome.Unknown;
        var apiKey = store.Get(CredentialKeys.PendingAuthKey);
        var agentId = store.Get(CredentialKeys.PendingAgentId);
        var pharmacyId = store.Get(CredentialKeys.PendingPharmacyId);
        var version = store.Get(CredentialKeys.PendingVersion);
        var cloudUrl = store.Get(CredentialKeys.PendingCloudUrl);
        if (new[] { apiKey, agentId, pharmacyId, version, cloudUrl }.Any(
                string.IsNullOrWhiteSpace))
            return AuthorityPromotionOutcome.Unknown;
        var config = new SetupConfig(
            PharmacyId: pharmacyId!,
            ApiKey: apiKey!,
            CloudUrl: cloudUrl!,
            ReleaseTag: "v" + version,
            LearningMode: false,
            AgentId: agentId!,
            DeviceCode: store.Get(CredentialKeys.PendingDeviceCode),
            DeviceKeyId: store.Get(CredentialKeys.PendingDeviceKeyId),
            DeviceKeyName: store.Get(CredentialKeys.PendingDeviceKeyName),
            DeviceFingerprint: store.Get(CredentialKeys.PendingDeviceFingerprint),
            DeviceChallenge: store.Get(CredentialKeys.PendingDeviceChallenge));
        return confirm(config, provisioningId);
    }

    internal static bool CompleteRecoveredPendingAuthority(string dataDirectory)
    {
        var store = CreateStore(dataDirectory);
        return CompleteRecoveredPendingAuthority(store);
    }

    internal static bool CompleteRecoveredPendingAuthority(
        IEncryptedCredentialStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var provisioningId = store.Get(CredentialKeys.PendingProvisioningId);
        if (provisioningId is null)
            return true;

        var pendingAuth = store.Get(CredentialKeys.PendingAuthKey);
        var pendingAgent = store.Get(CredentialKeys.PendingAgentId);
        var pendingPharmacy = store.Get(CredentialKeys.PendingPharmacyId);
        var pendingVersion = store.Get(CredentialKeys.PendingVersion);
        var pendingCloudUrl = store.Get(CredentialKeys.PendingCloudUrl);
        var pendingDeviceCode = store.Get(CredentialKeys.PendingDeviceCode);
        var pendingFingerprint = store.Get(CredentialKeys.PendingDeviceFingerprint);
        var pendingChallenge = store.Get(CredentialKeys.PendingDeviceChallenge);
        var pendingKeyName = store.Get(CredentialKeys.PendingDeviceKeyName);
        var pendingKeyId = store.Get(CredentialKeys.PendingDeviceKeyId);
        if (!Guid.TryParseExact(provisioningId, "D", out var parsedProvisioningId) ||
            !string.Equals(
                parsedProvisioningId.ToString("D"),
                provisioningId,
                StringComparison.Ordinal) ||
            new[]
            {
                pendingAuth,
                pendingAgent,
                pendingPharmacy,
                pendingVersion,
                pendingCloudUrl,
                pendingDeviceCode,
                pendingFingerprint,
                pendingChallenge,
                pendingKeyName,
                pendingKeyId,
            }.Any(string.IsNullOrWhiteSpace))
            return false;

        if (!string.Equals(
                store.Get(CredentialKeys.AuthKey),
                pendingAuth,
                StringComparison.Ordinal) ||
            !string.Equals(
                store.Get(CredentialKeys.AgentId),
                pendingAgent,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                store.Get(CredentialKeys.PharmacyId),
                pendingPharmacy,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                store.Get(CredentialKeys.DeviceKeyName),
                pendingKeyName,
                StringComparison.Ordinal) ||
            !string.Equals(
                store.Get(CredentialKeys.DeviceKeyId),
                pendingKeyId,
                StringComparison.Ordinal))
            return false;
        DeletePending(store);
        return true;
    }

    /// <summary>
    /// Reconciles protected pending authority when no durable install journal
    /// exists. A fully active target is only missing final cleanup. Otherwise
    /// the cloud confirmation point was never reachable, so the pending TPM slot
    /// is aborted before its DPAPI metadata is removed. When a newly approved
    /// pairing has reopened the same pending key, that key is retained for the
    /// replacement attempt while only the orphaned credential transaction is
    /// cleared.
    /// </summary>
    internal static bool ReconcilePendingAuthorityWithoutTransaction(
        string dataDirectory,
        SetupConfig? replacementConfig = null)
    {
        var store = CreateStore(dataDirectory);
        return ReconcilePendingAuthorityWithoutTransaction(
            store,
            DeviceAttestationKeyProvider.CreateProduction(),
            replacementConfig);
    }

    internal static bool ReconcilePendingAuthorityWithoutTransaction(
        IEncryptedCredentialStore store,
        IDeviceAttestationKeyProvider provider,
        SetupConfig? replacementConfig = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);
        var provisioningId = store.Get(CredentialKeys.PendingProvisioningId);
        if (provisioningId is null)
            return true;

        var pendingAuth = store.Get(CredentialKeys.PendingAuthKey);
        var pendingAgent = store.Get(CredentialKeys.PendingAgentId);
        var pendingPharmacy = store.Get(CredentialKeys.PendingPharmacyId);
        var pendingVersion = store.Get(CredentialKeys.PendingVersion);
        var pendingCloudUrl = store.Get(CredentialKeys.PendingCloudUrl);
        var pendingDeviceCode = store.Get(CredentialKeys.PendingDeviceCode);
        var pendingFingerprint = store.Get(CredentialKeys.PendingDeviceFingerprint);
        var pendingChallenge = store.Get(CredentialKeys.PendingDeviceChallenge);
        var pendingKeyName = store.Get(CredentialKeys.PendingDeviceKeyName);
        var pendingKeyId = store.Get(CredentialKeys.PendingDeviceKeyId);
        if (!Guid.TryParseExact(provisioningId, "D", out var parsedProvisioningId) ||
            parsedProvisioningId.ToString("D") != provisioningId ||
            new[]
            {
                pendingAuth,
                pendingAgent,
                pendingPharmacy,
                pendingVersion,
                pendingCloudUrl,
                pendingDeviceCode,
                pendingFingerprint,
                pendingChallenge,
                pendingKeyName,
                pendingKeyId,
            }.Any(string.IsNullOrWhiteSpace))
            return false;

        var activeAuth = store.Get(CredentialKeys.AuthKey);
        var activeKeyId = store.Get(CredentialKeys.DeviceKeyId);
        var activeIsExactTarget =
            string.Equals(activeAuth, pendingAuth, StringComparison.Ordinal) &&
            string.Equals(
                store.Get(CredentialKeys.AgentId),
                pendingAgent,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                store.Get(CredentialKeys.PharmacyId),
                pendingPharmacy,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                store.Get(CredentialKeys.DeviceKeyName),
                pendingKeyName,
                StringComparison.Ordinal) &&
            string.Equals(activeKeyId, pendingKeyId, StringComparison.Ordinal);
        if (activeIsExactTarget)
        {
            try
            {
                if (!provider.IsActiveVersion(
                        pendingFingerprint!,
                        pendingKeyName!,
                        pendingKeyId!))
                    return false;
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                                       CryptographicException or
                                       UnauthorizedAccessException)
            {
                return false;
            }
            DeletePending(store);
            return true;
        }

        // Either local authority field matching the target without the entire
        // target matching is an ambiguous partial finalization. Never compensate
        // by deleting a key that the cloud may already consider authoritative.
        if (string.Equals(activeAuth, pendingAuth, StringComparison.Ordinal) ||
            string.Equals(activeKeyId, pendingKeyId, StringComparison.Ordinal))
            return false;

        var replacementOwnsKey = replacementConfig is not null &&
            string.Equals(
                replacementConfig.DeviceFingerprint,
                pendingFingerprint,
                StringComparison.Ordinal) &&
            string.Equals(
                replacementConfig.DeviceKeyName,
                pendingKeyName,
                StringComparison.Ordinal) &&
            string.Equals(
                replacementConfig.DeviceKeyId,
                pendingKeyId,
                StringComparison.Ordinal);
        var replacementSupersedesMissingKey = false;
        if (!replacementOwnsKey &&
            replacementConfig is not null &&
            string.Equals(
                replacementConfig.DeviceFingerprint,
                pendingFingerprint,
                StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(replacementConfig.DeviceKeyName) &&
            !string.IsNullOrWhiteSpace(replacementConfig.DeviceKeyId))
        {
            try
            {
                replacementSupersedesMissingKey = provider.IsPendingVersion(
                    pendingFingerprint!,
                    replacementConfig.DeviceKeyName,
                    replacementConfig.DeviceKeyId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or
                                       CryptographicException or
                                       UnauthorizedAccessException)
            {
                return false;
            }
        }
        if (!replacementOwnsKey && !replacementSupersedesMissingKey)
            provider.AbortPending(pendingFingerprint!, pendingKeyId!);

        // Delete only after TPM cleanup (or exact replacement ownership) is
        // proven, retaining durable repair metadata on any key-management error.
        DeletePending(store);
        return true;
    }

    private static void PromotePending(IEncryptedCredentialStore store)
    {
        var auth = store.Get(CredentialKeys.PendingAuthKey)
            ?? throw new InvalidDataException("Pending authentication key is missing.");
        var agent = store.Get(CredentialKeys.PendingAgentId)
            ?? throw new InvalidDataException("Pending agent identity is missing.");
        var pharmacy = store.Get(CredentialKeys.PendingPharmacyId)
            ?? throw new InvalidDataException("Pending pharmacy identity is missing.");
        var active = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CredentialKeys.AuthKey] = auth,
            [CredentialKeys.AgentId] = agent,
            [CredentialKeys.PharmacyId] = pharmacy,
        };
        var keyName = store.Get(CredentialKeys.PendingDeviceKeyName);
        var keyId = store.Get(CredentialKeys.PendingDeviceKeyId);
        if (keyName is not null && keyId is not null)
        {
            active[CredentialKeys.DeviceKeyName] = keyName;
            active[CredentialKeys.DeviceKeyId] = keyId;
        }
        else if (keyName is not null || keyId is not null)
        {
            throw new InvalidDataException("Pending TPM key metadata is torn.");
        }
        store.SetMany(active);
    }

    private static void DeletePending(IEncryptedCredentialStore store)
    {
        store.DeleteMany([
            CredentialKeys.PendingAuthKey,
            CredentialKeys.PendingAgentId,
            CredentialKeys.PendingPharmacyId,
            CredentialKeys.PendingVersion,
            CredentialKeys.PendingCloudUrl,
            CredentialKeys.PendingProvisioningId,
            CredentialKeys.PendingDeviceCode,
            CredentialKeys.PendingDeviceFingerprint,
            CredentialKeys.PendingDeviceKeyName,
            CredentialKeys.PendingDeviceKeyId,
            CredentialKeys.PendingDeviceChallenge,
        ]);
    }
}
