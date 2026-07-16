using System.Security;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public sealed class CloudCredentialBootstrapperTests
{
    [Fact]
    public void ExistingStoreWinsOverStaleLegacyAppsettingsOnEveryRestart()
    {
        var store = new InMemoryCredentialStore();
        store.SetMany(new Dictionary<string, string>
        {
            [CredentialKeys.AuthKey] = "sagent_store_key",
            [CredentialKeys.AgentId] = AgentId,
        });
        var options = Options(apiKey: "sagent_stale_appsettings_key");

        var first = CloudCredentialBootstrapper.LoadOrMigrate(store, options, unprotectLegacyValue: false);
        options.ApiKey = "sagent_tampered_later";
        var restart = CloudCredentialBootstrapper.LoadOrMigrate(store, options, unprotectLegacyValue: false);

        Assert.Equal("sagent_store_key", first.AuthKey);
        Assert.Equal("sagent_store_key", restart.AuthKey);
        Assert.Equal(CloudCredentialSource.ProtectedStore, restart.Source);
        Assert.False(restart.MigrationAuditPending);
    }

    [Fact]
    public void LegacyMigrationCommitsKeyAndAuditMarkerInOneBatchAndIsIdempotent()
    {
        var inner = new InMemoryCredentialStore();
        var store = new RecordingStore(inner);
        var options = Options(apiKey: "sagent_legacy_key");

        var migrated = CloudCredentialBootstrapper.LoadOrMigrate(store, options, unprotectLegacyValue: false);
        var restart = CloudCredentialBootstrapper.LoadOrMigrate(store, options, unprotectLegacyValue: false);

        Assert.Equal(CloudCredentialSource.LegacyAppSettingsMigration, migrated.Source);
        Assert.True(migrated.MigrationAuditPending);
        Assert.Equal(CloudCredentialSource.ProtectedStore, restart.Source);
        Assert.True(restart.MigrationAuditPending);
        Assert.Single(store.BatchWrites);
        Assert.Contains(CredentialKeys.AuthKey, store.BatchWrites[0].Keys);
        Assert.Contains(CredentialKeys.AuthKeyMigrationAuditPending, store.BatchWrites[0].Keys);

        CloudCredentialBootstrapper.MarkMigrationAuditComplete(store);
        var auditedRestart = CloudCredentialBootstrapper.LoadOrMigrate(store, options, unprotectLegacyValue: false);
        Assert.False(auditedRestart.MigrationAuditPending);
    }

    [Fact]
    public void StoreIdentityMismatchFailsClosed()
    {
        var store = new InMemoryCredentialStore();
        store.SetMany(new Dictionary<string, string>
        {
            [CredentialKeys.AuthKey] = "sagent_store_key",
            [CredentialKeys.AgentId] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        });

        Assert.Throws<SecurityException>(() =>
            CloudCredentialBootstrapper.LoadOrMigrate(
                store,
                Options(apiKey: null),
                unprotectLegacyValue: false));
    }

    [Fact]
    public void MatchingTargetCohortUsesPendingCredentialWithoutReplacingActive()
    {
        var store = new InMemoryCredentialStore();
        store.SetMany(new Dictionary<string, string>
        {
            [CredentialKeys.AuthKey] = "sagent_active_key",
            [CredentialKeys.AgentId] = AgentId,
            [CredentialKeys.PharmacyId] = "PH-test",
            [CredentialKeys.DeviceKeyName] = "active-slot",
            [CredentialKeys.DeviceKeyId] = new string('a', 64),
            [CredentialKeys.PendingAuthKey] = "sagent_pending_key",
            [CredentialKeys.PendingAgentId] = AgentId,
            [CredentialKeys.PendingPharmacyId] = "PH-test",
            [CredentialKeys.PendingVersion] = "3.80.0",
            [CredentialKeys.PendingCloudUrl] = "https://suavollc.com",
            [CredentialKeys.PendingProvisioningId] = ProvisioningId,
            [CredentialKeys.PendingDeviceKeyName] = "pending-slot",
            [CredentialKeys.PendingDeviceKeyId] = new string('b', 64),
            [CredentialKeys.PendingDeviceFingerprint] = "machine-fingerprint",
            [CredentialKeys.PendingDeviceCode] = "pairing-code",
            [CredentialKeys.PendingDeviceChallenge] = "device-challenge",
        });
        var options = Options(apiKey: null);
        options.Version = "3.80.0";

        var result = CloudCredentialBootstrapper.LoadOrMigrate(
            store,
            options,
            unprotectLegacyValue: false);

        Assert.Equal("sagent_pending_key", result.AuthKey);
        Assert.Equal(CloudCredentialSource.PendingProvisioning, result.Source);
        Assert.Equal("pending-slot", result.DeviceKeyName);
        Assert.Equal(new string('b', 64), result.DeviceKeyId);
        Assert.Equal("sagent_active_key", store.Get(CredentialKeys.AuthKey));
    }

    [Fact]
    public void RolledBackVersionIgnoresPendingTargetAndUsesActiveCredential()
    {
        var store = new InMemoryCredentialStore();
        store.SetMany(new Dictionary<string, string>
        {
            [CredentialKeys.AuthKey] = "sagent_active_key",
            [CredentialKeys.AgentId] = AgentId,
            [CredentialKeys.PharmacyId] = "PH-test",
            [CredentialKeys.DeviceKeyName] = "active-slot",
            [CredentialKeys.DeviceKeyId] = new string('a', 64),
            [CredentialKeys.PendingAuthKey] = "sagent_pending_key",
            [CredentialKeys.PendingAgentId] = AgentId,
            [CredentialKeys.PendingPharmacyId] = "PH-test",
            [CredentialKeys.PendingVersion] = "3.80.0",
            [CredentialKeys.PendingCloudUrl] = "https://suavollc.com",
            [CredentialKeys.PendingProvisioningId] = ProvisioningId,
            [CredentialKeys.PendingDeviceKeyName] = "pending-slot",
            [CredentialKeys.PendingDeviceKeyId] = new string('b', 64),
            [CredentialKeys.PendingDeviceFingerprint] = "machine-fingerprint",
            [CredentialKeys.PendingDeviceCode] = "pairing-code",
            [CredentialKeys.PendingDeviceChallenge] = "device-challenge",
        });
        var options = Options(apiKey: null);
        options.Version = "3.79.0";

        var result = CloudCredentialBootstrapper.LoadOrMigrate(
            store,
            options,
            unprotectLegacyValue: false);

        Assert.Equal("sagent_active_key", result.AuthKey);
        Assert.Equal(CloudCredentialSource.ProtectedStore, result.Source);
        Assert.Equal("active-slot", result.DeviceKeyName);
        Assert.Equal(new string('a', 64), result.DeviceKeyId);
    }

    [Fact]
    public void StorePharmacyMismatchFailsClosed()
    {
        var store = new InMemoryCredentialStore();
        store.SetMany(new Dictionary<string, string>
        {
            [CredentialKeys.AuthKey] = "sagent_store_key",
            [CredentialKeys.AgentId] = AgentId,
            [CredentialKeys.PharmacyId] = "PH-other",
        });

        Assert.Throws<SecurityException>(() =>
            CloudCredentialBootstrapper.LoadOrMigrate(
                store,
                Options(apiKey: null),
                unprotectLegacyValue: false));
    }

    [Fact]
    public void IncompletePendingProvisioningFailsClosed()
    {
        var store = new InMemoryCredentialStore();
        store.Set(CredentialKeys.PendingAuthKey, "sagent_pending_key");

        Assert.Throws<InvalidDataException>(() =>
            CloudCredentialBootstrapper.LoadOrMigrate(
                store,
                Options(apiKey: null),
                unprotectLegacyValue: false));
    }

    [Fact]
    public void TornDeviceKeyMetadataFailsClosed()
    {
        var store = new InMemoryCredentialStore();
        store.SetMany(new Dictionary<string, string>
        {
            [CredentialKeys.AuthKey] = "sagent_store_key",
            [CredentialKeys.AgentId] = AgentId,
            [CredentialKeys.PharmacyId] = "PH-test",
            [CredentialKeys.DeviceKeyId] = new string('a', 64),
        });

        Assert.Throws<InvalidDataException>(() =>
            CloudCredentialBootstrapper.LoadOrMigrate(
                store,
                Options(apiKey: null),
                unprotectLegacyValue: false));
    }

    [Fact]
    public void PendingMarkerWithoutKeyFailsClosed()
    {
        var store = new InMemoryCredentialStore();
        store.Set(CredentialKeys.AuthKeyMigrationAuditPending, "legacy_appsettings_v1");

        Assert.Throws<InvalidDataException>(() =>
            CloudCredentialBootstrapper.LoadOrMigrate(
                store,
                Options(apiKey: null),
                unprotectLegacyValue: false));
    }

    [Fact]
    public void PlaintextSqlPasswordFailsClosedWhileDpapiEnvelopeIsAccepted()
    {
        var plaintext = Options(apiKey: null);
        plaintext.SqlPassword = "raw-password";
        Assert.Throws<SecurityException>(() =>
            CloudCredentialBootstrapper.ValidateSqlSecretsAreProtected(plaintext, enforce: true));

        plaintext.SqlPassword = "DPAPI:opaque-envelope";
        CloudCredentialBootstrapper.ValidateSqlSecretsAreProtected(plaintext, enforce: true);
    }

    private const string AgentId = "2a492d97-9b8c-4217-a5b1-142f8fa36602";
    private const string ProvisioningId = "11111111-1111-4111-8111-111111111111";

    private static AgentOptions Options(string? apiKey) => new()
    {
        ApiKey = apiKey,
        AgentId = AgentId,
        PharmacyId = "PH-test",
    };

    private sealed class RecordingStore : IEncryptedCredentialStore
    {
        private readonly IEncryptedCredentialStore _inner;

        public RecordingStore(IEncryptedCredentialStore inner) => _inner = inner;

        public List<IReadOnlyDictionary<string, string>> BatchWrites { get; } = [];
        public string? Get(string key) => _inner.Get(key);
        public void Set(string key, string value) => _inner.Set(key, value);
        public void SetMany(IReadOnlyDictionary<string, string> values)
        {
            BatchWrites.Add(new Dictionary<string, string>(values));
            _inner.SetMany(values);
        }
        public void Delete(string key) => _inner.Delete(key);
        public void DeleteMany(IReadOnlyCollection<string> keys) => _inner.DeleteMany(keys);
    }
}
