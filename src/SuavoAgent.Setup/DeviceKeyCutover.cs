using System.Collections.Concurrent;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup;

/// <summary>
/// Owns the pending-to-active TPM key transition for Setup. Merely opening the
/// installer or requesting approval can never replace the healthy Core key.
/// </summary>
internal static class DeviceKeyCutover
{
    private sealed record Pending(
        string Fingerprint,
        string KeyId,
        IDeviceAttestationKeyProvider Provider);

    private static readonly ConcurrentDictionary<string, Pending> Tracked =
        new(StringComparer.Ordinal);

    static DeviceKeyCutover()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AbortAllBestEffort();
    }

    internal static void Track(
        SetupConfig config,
        string fingerprint,
        IDeviceAttestationKeyProvider? provider = null)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceKeyId)) return;
        provider ??= DeviceAttestationKeyProvider.CreateProduction();
        var pending = new Pending(fingerprint, config.DeviceKeyId, provider);
        Tracked.AddOrUpdate(config.DeviceKeyId, pending, (_, _) => pending);
    }

    internal static void Commit(SetupConfig config, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceKeyId)) return;
        var pending = Resolve(config.DeviceKeyId, fingerprint);
        pending.Provider.CommitPending(pending.Fingerprint, pending.KeyId);
        Tracked.TryRemove(pending.KeyId, out _);
    }

    /// <summary>
    /// Once the DPAPI probation transaction is durable, process teardown must
    /// not delete its key. A restarted matching cohort can reopen the exact
    /// pending name/id and reconcile an interrupted cloud confirmation.
    /// </summary>
    internal static void PreserveForRecovery(SetupConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.DeviceKeyId))
            Tracked.TryRemove(config.DeviceKeyId, out _);
    }

    internal static void Abort(SetupConfig config, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(config.DeviceKeyId)) return;
        var pending = Resolve(config.DeviceKeyId, fingerprint);
        try
        {
            pending.Provider.AbortPending(pending.Fingerprint, pending.KeyId);
        }
        finally
        {
            Tracked.TryRemove(pending.KeyId, out _);
        }
    }

    private static Pending Resolve(string keyId, string fingerprint)
    {
        if (Tracked.TryGetValue(keyId, out var pending))
        {
            if (!string.Equals(pending.Fingerprint, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Pending TPM key fingerprint mismatch.");
            return pending;
        }
        return new(fingerprint, keyId, DeviceAttestationKeyProvider.CreateProduction());
    }

    private static void AbortAllBestEffort()
    {
        foreach (var pending in Tracked.Values)
        {
            try
            {
                pending.Provider.AbortPending(pending.Fingerprint, pending.KeyId);
            }
            catch
            {
                // Process teardown cannot surface UI. The active slot was never
                // changed; a later Setup run can safely recover stale pending state.
            }
            Tracked.TryRemove(pending.KeyId, out _);
        }
    }
}
