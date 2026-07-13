using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace SuavoAgent.Contracts.Security;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsTpmMaintenanceAttestationKeyProvider
{
    private const int MaxOrphanKeys = 16;
    private const int NcryptMachineKeyFlag = 0x20;
    private const int NcryptSilentFlag = 0x40;
    private const int NteNoMoreItems = unchecked((int)0x8009002A);

    [StructLayout(LayoutKind.Sequential)]
    private struct NCryptKeyName
    {
        internal IntPtr Name;
        internal IntPtr AlgorithmId;
        internal int LegacyKeySpec;
        internal int Flags;
    }

    private static void CleanupOrphanedKeys(string authoritativeFingerprint)
    {
        var prefix = MaintenanceAttestationKeyProvider.KeyPrefix(
                         authoritativeFingerprint) + ".slot.";
        var provider = IntPtr.Zero;
        var enumerationState = IntPtr.Zero;
        var names = new List<string>();
        try
        {
            ThrowNcryptFailure(NCryptOpenStorageProvider(
                out provider,
                "Microsoft Platform Crypto Provider",
                0));
            while (true)
            {
                var status = NCryptEnumKeys(
                    provider,
                    null,
                    out var keyNamePointer,
                    ref enumerationState,
                    NcryptMachineKeyFlag | NcryptSilentFlag);
                if (status == NteNoMoreItems) break;
                ThrowNcryptFailure(status);
                try
                {
                    var keyName = Marshal.PtrToStructure<NCryptKeyName>(keyNamePointer);
                    var name = Marshal.PtrToStringUni(keyName.Name);
                    if (name is not null && IsExactOrphanName(name, prefix))
                    {
                        if (names.Count >= MaxOrphanKeys)
                            throw new InvalidOperationException(
                                "Too many orphaned TPM maintenance keys require manual repair.");
                        names.Add(name);
                    }
                }
                finally
                {
                    if (keyNamePointer != IntPtr.Zero) NCryptFreeBuffer(keyNamePointer);
                }
            }

            foreach (var name in names)
            {
                using var key = CngKey.Open(
                    name,
                    PlatformProvider,
                    CngKeyOpenOptions.MachineKey);
                if (key.Algorithm != CngAlgorithm.ECDsaP256 ||
                    key.AlgorithmGroup != CngAlgorithmGroup.ECDsa ||
                    key.Provider != PlatformProvider ||
                    key.KeyUsage != CngKeyUsages.Signing ||
                    key.ExportPolicy != CngExportPolicies.None)
                    throw new InvalidOperationException(
                        "An orphaned maintenance key has an invalid TPM policy.");
                AssertMaintenanceAcl(key);
                key.Delete();
            }
        }
        finally
        {
            if (enumerationState != IntPtr.Zero) NCryptFreeBuffer(enumerationState);
            if (provider != IntPtr.Zero) NCryptFreeObject(provider);
        }
    }

    private static bool IsExactOrphanName(string name, string prefix)
    {
        if (name.Length != prefix.Length + 32 ||
            !name.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        foreach (var character in name.AsSpan(prefix.Length))
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        return true;
    }

    private static void ThrowNcryptFailure(int status)
    {
        if (status != 0)
            throw new CryptographicException(status);
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider(
        out IntPtr provider,
        string providerName,
        int flags);

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptEnumKeys(
        IntPtr provider,
        string? scope,
        out IntPtr keyName,
        ref IntPtr enumerationState,
        int flags);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeBuffer(IntPtr pointer);

    [DllImport("ncrypt.dll")]
    private static extern int NCryptFreeObject(IntPtr handle);
}
