using System.Security;
using System.Security.AccessControl;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Config;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Maintenance;
using SuavoAgent.Setup.Security;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class InitialCredentialPersisterTests
{
    [Fact]
    public void StagesThenPromotesAuthAndIdentityWithoutReplacingActiveEarly()
    {
        var inner = new InMemoryCredentialStore();
        var store = new RecordingStore(inner);
        var config = Config("11111111-1111-1111-1111-111111111111", "sagent_initial");

        var provisioningId = InitialCredentialPersister.Stage(store, config);

        Assert.Single(store.Batches);
        Assert.Null(inner.Get(CredentialKeys.AuthKey));
        Assert.Equal("sagent_initial", inner.Get(CredentialKeys.PendingAuthKey));
        Assert.Equal(config.AgentId, inner.Get(CredentialKeys.PendingAgentId));
        Assert.Equal(config.PharmacyId, inner.Get(CredentialKeys.PendingPharmacyId));
        Assert.Equal("3.80.0", inner.Get(CredentialKeys.PendingVersion));
        Assert.Equal(provisioningId, inner.Get(CredentialKeys.PendingProvisioningId));
        Assert.True(Guid.TryParse(provisioningId, out _));
        Assert.Equal(config.DeviceCode, inner.Get(CredentialKeys.PendingDeviceCode));
        Assert.Equal(config.DeviceKeyName, inner.Get(CredentialKeys.PendingDeviceKeyName));
        Assert.Equal(config.DeviceKeyId, inner.Get(CredentialKeys.PendingDeviceKeyId));
        Assert.Contains(CredentialKeys.PendingDeviceKeyId, store.Batches[0].Keys);

        InitialCredentialPersister.Commit(store, config);

        Assert.Equal("sagent_initial", inner.Get(CredentialKeys.AuthKey));
        Assert.Equal(config.AgentId, inner.Get(CredentialKeys.AgentId));
        Assert.Equal(config.PharmacyId, inner.Get(CredentialKeys.PharmacyId));
        Assert.Equal(config.DeviceKeyName, inner.Get(CredentialKeys.DeviceKeyName));
        Assert.Equal(config.DeviceKeyId, inner.Get(CredentialKeys.DeviceKeyId));
        Assert.Equal("sagent_initial", inner.Get(CredentialKeys.PendingAuthKey));
        Assert.Equal(provisioningId, inner.Get(CredentialKeys.PendingProvisioningId));

        InitialCredentialPersister.Complete(store, config);

        Assert.Null(inner.Get(CredentialKeys.PendingAuthKey));
        Assert.Null(inner.Get(CredentialKeys.PendingProvisioningId));
        Assert.Null(inner.Get(CredentialKeys.PendingDeviceKeyId));
        var atomicDelete = Assert.Single(store.DeleteBatches);
        Assert.Equal(11, atomicDelete.Count);
        Assert.Contains(CredentialKeys.PendingProvisioningId, atomicDelete);
        Assert.Contains(CredentialKeys.PendingDeviceKeyId, atomicDelete);
    }

    [Fact]
    public void ReinstallOfSameIdentityRotatesCredentialButDifferentIdentityFailsClosed()
    {
        var store = new InMemoryCredentialStore();
        var first = Config("11111111-1111-1111-1111-111111111111", "sagent_first");
        InitialCredentialPersister.Stage(store, first);
        InitialCredentialPersister.Commit(store, first);
        InitialCredentialPersister.Complete(store, first);
        var rotated = Config("11111111-1111-1111-1111-111111111111", "sagent_rotated");
        InitialCredentialPersister.Stage(store, rotated);

        Assert.Equal("sagent_first", store.Get(CredentialKeys.AuthKey));
        Assert.Equal("sagent_rotated", store.Get(CredentialKeys.PendingAuthKey));
        InitialCredentialPersister.Commit(store, rotated);
        InitialCredentialPersister.Complete(store, rotated);
        Assert.Equal("sagent_rotated", store.Get(CredentialKeys.AuthKey));
        Assert.Throws<SecurityException>(() => InitialCredentialPersister.Stage(
            store,
            Config("22222222-2222-2222-2222-222222222222", "sagent_other")));
        Assert.Equal("sagent_rotated", store.Get(CredentialKeys.AuthKey));
    }

    [Fact]
    public void SameAgentCannotBeSilentlyMovedToAnotherPharmacy()
    {
        var store = new InMemoryCredentialStore();
        var first = Config("11111111-1111-1111-1111-111111111111", "sagent_first");
        InitialCredentialPersister.Stage(store, first);
        InitialCredentialPersister.Commit(store, first);
        InitialCredentialPersister.Complete(store, first);

        Assert.Throws<SecurityException>(() => InitialCredentialPersister.Stage(
            store,
            Config(first.AgentId, "sagent_other", pharmacyId: "PH-other")));
        Assert.Equal("PH-test", store.Get(CredentialKeys.PharmacyId));
    }

    [Fact]
    public void AbortRemovesOnlyMatchingPendingTransactionAndPreservesActive()
    {
        var store = new InMemoryCredentialStore();
        var first = Config("11111111-1111-1111-1111-111111111111", "sagent_first");
        InitialCredentialPersister.Stage(store, first);
        InitialCredentialPersister.Commit(store, first);
        InitialCredentialPersister.Complete(store, first);
        var rotated = Config(first.AgentId, "sagent_rotated");
        InitialCredentialPersister.Stage(store, rotated);

        InitialCredentialPersister.Abort(store, rotated);

        Assert.Equal("sagent_first", store.Get(CredentialKeys.AuthKey));
        Assert.Null(store.Get(CredentialKeys.PendingAuthKey));
        Assert.Null(store.Get(CredentialKeys.PendingDeviceKeyName));
        Assert.Null(store.Get(CredentialKeys.PendingDeviceKeyId));
    }

    [Fact]
    public void CrashAfterCloudPromotionBeforeLocalCommit_RecoversForwardFromPendingJournal()
    {
        const string fingerprint = "crash-recovery-fingerprint";
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var oldPending = provider.OpenOrCreate(fingerprint);
        provider.CommitPending(fingerprint, oldPending.Enrollment.KeyId);
        using var oldActive = provider.OpenExisting(fingerprint);
        var oldKeyId = oldActive.Enrollment.KeyId;

        var store = new InMemoryCredentialStore();
        store.SetMany(new Dictionary<string, string>
        {
            [CredentialKeys.AuthKey] = "sagent_old",
            [CredentialKeys.AgentId] = "11111111-1111-1111-1111-111111111111",
            [CredentialKeys.PharmacyId] = "PH-test",
            [CredentialKeys.DeviceKeyName] = oldActive.LocalKeyName,
            [CredentialKeys.DeviceKeyId] = oldKeyId,
        });
        using var nextPending = provider.OpenOrCreate(fingerprint);
        var config = Config(
            "11111111-1111-1111-1111-111111111111",
            "sagent_new") with
        {
            DeviceKeyName = nextPending.LocalKeyName,
            DeviceKeyId = nextPending.Enrollment.KeyId,
            DeviceFingerprint = fingerprint,
        };
        var provisioningId = InitialCredentialPersister.Stage(store, config);

        // Cloud promotion succeeded and the process died before local Commit.
        Assert.Equal("sagent_old", store.Get(CredentialKeys.AuthKey));
        Assert.Equal("sagent_new", store.Get(CredentialKeys.PendingAuthKey));
        Assert.Equal(provisioningId, store.Get(CredentialKeys.PendingProvisioningId));
        Assert.Equal(oldKeyId, provider.OpenExisting(fingerprint).Enrollment.KeyId);

        var replayed = InitialCredentialPersister.ReplayPendingAuthorityPromotion(
            store,
            (recoveredConfig, recoveredProvisioningId) =>
            {
                Assert.Equal(config, recoveredConfig);
                Assert.Equal(provisioningId, recoveredProvisioningId);
                return AuthorityPromotionOutcome.Promoted;
            });
        var finalized = InitialCredentialPersister.FinalizePendingAuthority(store, provider);
        var completed = InitialCredentialPersister.CompleteRecoveredPendingAuthority(store);

        Assert.Equal(AuthorityPromotionOutcome.Promoted, replayed);
        Assert.True(finalized);
        Assert.True(completed);
        Assert.Equal("sagent_new", store.Get(CredentialKeys.AuthKey));
        Assert.Equal(config.DeviceKeyId, provider.OpenExisting(fingerprint).Enrollment.KeyId);
        Assert.Null(store.Get(CredentialKeys.PendingProvisioningId));
    }

    [Fact]
    public void RecoveryNeverMistakesOldActiveCredentialForMissingTargetJournal()
    {
        var store = new InMemoryCredentialStore();
        store.SetMany(new Dictionary<string, string>
        {
            [CredentialKeys.AuthKey] = "sagent_predecessor",
            [CredentialKeys.AgentId] = "11111111-1111-1111-1111-111111111111",
            [CredentialKeys.PharmacyId] = "PH-test",
            [CredentialKeys.DeviceKeyName] = "old-key",
            [CredentialKeys.DeviceKeyId] = new string('a', 64),
        });
        var confirmCalls = 0;

        var promotion = InitialCredentialPersister.ReplayPendingAuthorityPromotion(
            store,
            (_, _) =>
            {
                confirmCalls++;
                return AuthorityPromotionOutcome.Promoted;
            });
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        var finalized = InitialCredentialPersister.FinalizePendingAuthority(store, provider);

        Assert.Equal(AuthorityPromotionOutcome.Unknown, promotion);
        Assert.Equal(0, confirmCalls);
        Assert.False(finalized);
        Assert.Equal("sagent_predecessor", store.Get(CredentialKeys.AuthKey));
    }

    [Fact]
    public void RecoveredCleanupRequiresCompleteExactTargetProofAndPreservesMismatch()
    {
        var store = new InMemoryCredentialStore();
        var config = Config(
            "11111111-1111-1111-1111-111111111111",
            "sagent_target");
        var provisioningId = InitialCredentialPersister.Stage(store, config);

        Assert.False(InitialCredentialPersister.CompleteRecoveredPendingAuthority(store));
        Assert.Equal(provisioningId, store.Get(CredentialKeys.PendingProvisioningId));

        InitialCredentialPersister.Commit(store, config);
        store.Delete(CredentialKeys.PendingDeviceFingerprint);

        Assert.False(InitialCredentialPersister.CompleteRecoveredPendingAuthority(store));
        Assert.Equal(provisioningId, store.Get(CredentialKeys.PendingProvisioningId));
        Assert.Equal(config.DeviceKeyId, store.Get(CredentialKeys.DeviceKeyId));
    }

    [Fact]
    public void RecoveredCleanupWithNoPendingTargetIsNormalNoOp()
    {
        var store = new InMemoryCredentialStore();

        Assert.True(InitialCredentialPersister.CompleteRecoveredPendingAuthority(store));
        Assert.Null(store.Get(CredentialKeys.AuthKey));
        Assert.Null(store.Get(CredentialKeys.PendingProvisioningId));
    }

    [Fact]
    public void NoJournalOrphanAbortsPendingKeyBeforeDeletingProtectedMetadata()
    {
        const string fingerprint = "pre-journal-crash";
        var store = new InMemoryCredentialStore();
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var pending = provider.OpenOrCreate(fingerprint);
        var config = Config(
            "11111111-1111-1111-1111-111111111111",
            "sagent_orphan") with
        {
            DeviceFingerprint = fingerprint,
            DeviceKeyName = pending.LocalKeyName,
            DeviceKeyId = pending.Enrollment.KeyId,
        };
        InitialCredentialPersister.Stage(store, config);

        var reconciled = InitialCredentialPersister
            .ReconcilePendingAuthorityWithoutTransaction(store, provider);

        Assert.True(reconciled);
        Assert.Null(store.Get(CredentialKeys.PendingProvisioningId));
        Assert.Throws<InvalidOperationException>(() => provider.OpenVersion(
            fingerprint,
            pending.LocalKeyName,
            pending.Enrollment.KeyId));
    }

    [Fact]
    public void RestartPairingMayRetainExactReopenedKeyWhileClearingOldCredentialAttempt()
    {
        const string fingerprint = "restart-pairing";
        var store = new InMemoryCredentialStore();
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var pending = provider.OpenOrCreate(fingerprint);
        var crashed = Config(
            "11111111-1111-1111-1111-111111111111",
            "sagent_crashed") with
        {
            DeviceFingerprint = fingerprint,
            DeviceKeyName = pending.LocalKeyName,
            DeviceKeyId = pending.Enrollment.KeyId,
        };
        InitialCredentialPersister.Stage(store, crashed);
        var replacement = crashed with
        {
            ApiKey = "sagent_replacement",
            DeviceCode = "WXYZ-6789",
        };

        var reconciled = InitialCredentialPersister
            .ReconcilePendingAuthorityWithoutTransaction(
                store,
                provider,
                replacement);

        Assert.True(reconciled);
        Assert.Null(store.Get(CredentialKeys.PendingProvisioningId));
        using var retained = provider.OpenVersion(
            fingerprint,
            pending.LocalKeyName,
            pending.Enrollment.KeyId);
        Assert.Equal(pending.Enrollment.KeyId, retained.Enrollment.KeyId);
    }

    [Fact]
    public void RestartPairingMayReplaceKeyAlreadyAbortedDuringGracefulProcessExit()
    {
        const string fingerprint = "graceful-exit-repair";
        var store = new InMemoryCredentialStore();
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var oldPending = provider.OpenOrCreate(fingerprint);
        var crashed = Config(
            "11111111-1111-1111-1111-111111111111",
            "sagent_crashed") with
        {
            DeviceFingerprint = fingerprint,
            DeviceKeyName = oldPending.LocalKeyName,
            DeviceKeyId = oldPending.Enrollment.KeyId,
        };
        InitialCredentialPersister.Stage(store, crashed);
        provider.AbortPending(fingerprint, oldPending.Enrollment.KeyId);
        using var replacementPending = provider.OpenOrCreate(fingerprint);
        var replacement = crashed with
        {
            ApiKey = "sagent_replacement",
            DeviceCode = "WXYZ-6789",
            DeviceKeyName = replacementPending.LocalKeyName,
            DeviceKeyId = replacementPending.Enrollment.KeyId,
        };

        var reconciled = InitialCredentialPersister
            .ReconcilePendingAuthorityWithoutTransaction(
                store,
                provider,
                replacement);

        Assert.True(reconciled);
        Assert.Null(store.Get(CredentialKeys.PendingProvisioningId));
        using var retained = provider.OpenVersion(
            fingerprint,
            replacementPending.LocalKeyName,
            replacementPending.Enrollment.KeyId);
        Assert.Equal(replacementPending.Enrollment.KeyId, retained.Enrollment.KeyId);
    }

    [Fact]
    public void AmbiguousLocalPromotionWithoutJournalIsPreservedFailClosed()
    {
        const string fingerprint = "ambiguous-promotion";
        var store = new InMemoryCredentialStore();
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var pending = provider.OpenOrCreate(fingerprint);
        var config = Config(
            "11111111-1111-1111-1111-111111111111",
            "sagent_target") with
        {
            DeviceFingerprint = fingerprint,
            DeviceKeyName = pending.LocalKeyName,
            DeviceKeyId = pending.Enrollment.KeyId,
        };
        InitialCredentialPersister.Stage(store, config);
        store.Set(CredentialKeys.AuthKey, config.ApiKey);

        var reconciled = InitialCredentialPersister
            .ReconcilePendingAuthorityWithoutTransaction(store, provider);

        Assert.False(reconciled);
        Assert.NotNull(store.Get(CredentialKeys.PendingProvisioningId));
        using var preserved = provider.OpenVersion(
            fingerprint,
            pending.LocalKeyName,
            pending.Enrollment.KeyId);
        Assert.Equal(pending.Enrollment.KeyId, preserved.Enrollment.KeyId);
    }

    [Fact]
    public void NoJournalCleanupRequiresDpapiAndTpmToProveSameActiveTarget()
    {
        const string fingerprint = "completed-before-cleanup";
        var store = new InMemoryCredentialStore();
        using var provider = new InMemoryDeviceAttestationKeyProvider();
        using var pending = provider.OpenOrCreate(fingerprint);
        var config = Config(
            "11111111-1111-1111-1111-111111111111",
            "sagent_target") with
        {
            DeviceFingerprint = fingerprint,
            DeviceKeyName = pending.LocalKeyName,
            DeviceKeyId = pending.Enrollment.KeyId,
        };
        InitialCredentialPersister.Stage(store, config);
        InitialCredentialPersister.Commit(store, config);

        Assert.False(InitialCredentialPersister
            .ReconcilePendingAuthorityWithoutTransaction(store, provider));
        Assert.NotNull(store.Get(CredentialKeys.PendingProvisioningId));

        provider.CommitPending(fingerprint, pending.Enrollment.KeyId);
        Assert.True(InitialCredentialPersister
            .ReconcilePendingAuthorityWithoutTransaction(store, provider));
        Assert.Null(store.Get(CredentialKeys.PendingProvisioningId));
    }

    [Fact]
    public void CredentialPathIsUnderMutableDataAclWhileInstallAclRemainsReadExecuteOnly()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "ProgramData", "SuavoAgent");
        var credentialPath = InitialCredentialPersister.CredentialPath(dataDirectory);
        var installAcl = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Install,
            directory: true,
            inherit: true);
        var dataAcl = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Data,
            directory: true,
            inherit: true);

        Assert.EndsWith(Path.Combine("SuavoAgent", "credentials.dat"), credentialPath);
        Assert.Contains(installAcl.Aces, ace =>
            ace.Sid == CoreServiceIdentity.ServiceSid &&
            ace.Rights == FileSystemRights.ReadAndExecute);
        Assert.DoesNotContain(installAcl.Aces, ace =>
            ace.Sid == CoreServiceIdentity.ServiceSid &&
            ace.Rights == FileSystemRights.Modify);
        Assert.Contains(dataAcl.Aces, ace =>
            ace.Sid == CoreServiceIdentity.ServiceSid &&
            ace.Rights == FileSystemRights.Modify);
    }

    private static SetupConfig Config(
        string agentId,
        string apiKey,
        string pharmacyId = "PH-test") => new(
        PharmacyId: pharmacyId,
        ApiKey: apiKey,
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.80.0",
        LearningMode: false,
        AgentId: agentId,
        DeviceCode: "ABCD-2345",
        DeviceKeyId: new string('a', 64),
        DeviceKeyName: "SuavoAgent.DeviceAuthority.v1.test.slot.pending",
        DeviceFingerprint: "test-machine-fingerprint",
        DeviceChallenge: new string('A', 43));

    private sealed class RecordingStore : IEncryptedCredentialStore
    {
        private readonly IEncryptedCredentialStore _inner;
        public RecordingStore(IEncryptedCredentialStore inner) => _inner = inner;
        public List<IReadOnlyDictionary<string, string>> Batches { get; } = [];
        public List<IReadOnlyCollection<string>> DeleteBatches { get; } = [];
        public string? Get(string key) => _inner.Get(key);
        public void Set(string key, string value) => _inner.Set(key, value);
        public void SetMany(IReadOnlyDictionary<string, string> values)
        {
            Batches.Add(new Dictionary<string, string>(values));
            _inner.SetMany(values);
        }
        public void Delete(string key) => _inner.Delete(key);
        public void DeleteMany(IReadOnlyCollection<string> keys)
        {
            DeleteBatches.Add(keys.ToArray());
            _inner.DeleteMany(keys);
        }
    }
}
