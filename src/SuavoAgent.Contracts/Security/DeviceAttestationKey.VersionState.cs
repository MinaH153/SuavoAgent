using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace SuavoAgent.Contracts.Security;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsTpmDeviceAttestationKeyProvider
{
    public bool IsActiveVersion(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId) =>
        SlotMatches(
            authoritativeFingerprint,
            ActiveValue,
            expectedKeyName,
            expectedKeyId);

    public bool IsPendingVersion(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId) =>
        SlotMatches(
            authoritativeFingerprint,
            PendingValue,
            expectedKeyName,
            expectedKeyId);

    private bool SlotMatches(
        string authoritativeFingerprint,
        string slotName,
        string expectedKeyName,
        string expectedKeyId)
    {
        using var crossProcess = DeviceAuthorityCrossProcessLock.Acquire(
            authoritativeFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKeyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedKeyId);
        var statePath = StatePath(authoritativeFingerprint);
        lock (_gate)
        {
            var slot = ReadSlot(statePath, slotName);
            if (slot is null) return false;
            ValidateEnrollment(slot);
            return string.Equals(slot.KeyName, expectedKeyName, StringComparison.Ordinal) &&
                   string.Equals(slot.KeyId, expectedKeyId, StringComparison.Ordinal) &&
                   CngKey.Exists(
                       slot.KeyName,
                       PlatformProvider,
                       CngKeyOpenOptions.MachineKey);
        }
    }
}

public sealed partial class InMemoryDeviceAttestationKeyProvider
{
    public bool IsActiveVersion(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId) =>
        VersionMatches(
            authoritativeFingerprint,
            expectedKeyName,
            expectedKeyId,
            pending: false);

    public bool IsPendingVersion(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId) =>
        VersionMatches(
            authoritativeFingerprint,
            expectedKeyName,
            expectedKeyId,
            pending: true);

    private bool VersionMatches(
        string authoritativeFingerprint,
        string expectedKeyName,
        string expectedKeyId,
        bool pending)
    {
        var name = DeviceAttestationKeyProvider.KeyName(authoritativeFingerprint);
        if (!_keys.TryGetValue(name, out var state)) return false;
        var key = pending ? state.Pending : state.Active;
        return key is not null &&
               string.Equals(LocalName(key), expectedKeyName, StringComparison.Ordinal) &&
               string.Equals(key.Enrollment.KeyId, expectedKeyId, StringComparison.Ordinal);
    }
}
