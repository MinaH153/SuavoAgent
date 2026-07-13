using System.Reflection;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DeviceKeyCutoverCollection
{
    public const string Name = "DeviceKeyCutover serial";
}

[Collection(DeviceKeyCutoverCollection.Name)]
public sealed class DeviceKeyCutoverTests
{
    [Fact]
    public void EmptyKeyIdentity_IsAnIntentionalNoOp()
    {
        var provider = new RecordingProvider();
        var config = Config(keyId: null);

        DeviceKeyCutover.Track(config, "machine-a", provider);
        DeviceKeyCutover.Commit(config, "machine-a");
        DeviceKeyCutover.Abort(config, "machine-a");
        DeviceKeyCutover.PreserveForRecovery(config);

        Assert.Empty(provider.Calls);
    }

    [Fact]
    public void Commit_PromotesOnlyTheTrackedFingerprintAndKey()
    {
        var provider = new RecordingProvider();
        var config = Config(UniqueKey());

        DeviceKeyCutover.Track(config, "machine-a", provider);
        DeviceKeyCutover.Commit(config, "machine-a");

        Assert.Equal(["commit:machine-a:" + config.DeviceKeyId], provider.Calls);
    }

    [Fact]
    public void ReTrackingSameKey_ReplacesStaleProviderAtomically()
    {
        var stale = new RecordingProvider();
        var current = new RecordingProvider();
        var config = Config(UniqueKey());

        DeviceKeyCutover.Track(config, "machine-a", stale);
        DeviceKeyCutover.Track(config, "machine-a", current);
        DeviceKeyCutover.Abort(config, "machine-a");

        Assert.Empty(stale.Calls);
        Assert.Equal(["abort:machine-a:" + config.DeviceKeyId], current.Calls);
    }

    [Fact]
    public void FingerprintMismatch_FailsClosedWithoutLosingPendingAuthority()
    {
        var provider = new RecordingProvider();
        var config = Config(UniqueKey());
        DeviceKeyCutover.Track(config, "machine-a", provider);

        var error = Assert.Throws<InvalidOperationException>(
            () => DeviceKeyCutover.Commit(config, "machine-b"));
        Assert.Contains("fingerprint mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(provider.Calls);

        DeviceKeyCutover.Abort(config, "machine-a");
        Assert.Equal(["abort:machine-a:" + config.DeviceKeyId], provider.Calls);
    }

    [Fact]
    public void Abort_RemovesTrackingEvenWhenProviderCleanupThrows()
    {
        var failing = new RecordingProvider { ThrowOnAbort = true };
        var config = Config(UniqueKey());
        DeviceKeyCutover.Track(config, "machine-a", failing);

        Assert.Throws<InvalidOperationException>(
            () => DeviceKeyCutover.Abort(config, "machine-a"));

        var replacement = new RecordingProvider();
        DeviceKeyCutover.Track(config, "machine-a", replacement);
        DeviceKeyCutover.Commit(config, "machine-a");
        Assert.Equal(["commit:machine-a:" + config.DeviceKeyId], replacement.Calls);
    }

    [Fact]
    public void PreserveForRecovery_UntracksWithoutDeletingDurablePendingKey()
    {
        var provider = new RecordingProvider();
        var config = Config(UniqueKey());

        DeviceKeyCutover.Track(config, "machine-a", provider);
        DeviceKeyCutover.PreserveForRecovery(config);

        Assert.Empty(provider.Calls);
    }

    [Fact]
    public void ProcessExitCleanup_AttemptsEveryPendingKeyAndContainsFailures()
    {
        var first = new RecordingProvider { ThrowOnAbort = true };
        var second = new RecordingProvider();
        var firstConfig = Config(UniqueKey());
        var secondConfig = Config(UniqueKey());
        DeviceKeyCutover.Track(firstConfig, "machine-a", first);
        DeviceKeyCutover.Track(secondConfig, "machine-b", second);

        var cleanup = typeof(DeviceKeyCutover).GetMethod(
            "AbortAllBestEffort",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(cleanup);
        cleanup.Invoke(null, null);

        Assert.Equal(["abort:machine-a:" + firstConfig.DeviceKeyId], first.Calls);
        Assert.Equal(["abort:machine-b:" + secondConfig.DeviceKeyId], second.Calls);
    }

    private static SetupConfig Config(string? keyId) => new(
        PharmacyId: "11111111-1111-4111-8111-111111111111",
        ApiKey: "sagent_test_key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.80.0",
        LearningMode: false,
        AgentId: "22222222-2222-4222-8222-222222222222",
        DeviceKeyId: keyId);

    private static string UniqueKey() =>
        Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private sealed class RecordingProvider : IDeviceAttestationKeyProvider
    {
        internal List<string> Calls { get; } = [];
        internal bool ThrowOnAbort { get; init; }

        public void CommitPending(string authoritativeFingerprint, string expectedKeyId) =>
            Calls.Add($"commit:{authoritativeFingerprint}:{expectedKeyId}");

        public void AbortPending(string authoritativeFingerprint, string expectedKeyId)
        {
            Calls.Add($"abort:{authoritativeFingerprint}:{expectedKeyId}");
            if (ThrowOnAbort) throw new InvalidOperationException("injected cleanup failure");
        }

        public IDeviceAttestationKey OpenOrCreate(string authoritativeFingerprint) =>
            throw new NotSupportedException();
        public IDeviceAttestationKey OpenExisting(string authoritativeFingerprint) =>
            throw new NotSupportedException();
        public IDeviceAttestationKey OpenExistingForMaintenance(string authoritativeFingerprint) =>
            throw new NotSupportedException();
        public DeviceMaintenanceSignature SignForMaintenance(
            string authoritativeFingerprint,
            string expectedActiveKeyId,
            ReadOnlyMemory<byte> canonicalBytes) => throw new NotSupportedException();
        public IDeviceAttestationKey OpenVersion(
            string authoritativeFingerprint,
            string expectedKeyName,
            string expectedKeyId) => throw new NotSupportedException();
        public bool IsActiveVersion(
            string authoritativeFingerprint,
            string expectedKeyName,
            string expectedKeyId) => false;
        public bool IsPendingVersion(
            string authoritativeFingerprint,
            string expectedKeyName,
            string expectedKeyId) => false;
        public void DestroyForUninstall(
            string authoritativeFingerprint,
            string expectedActiveKeyId) => throw new NotSupportedException();
    }
}
