using System.Runtime.Versioning;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public sealed class CredentialStoreTests
{
    [Fact]
    public void InMemory_RoundTrips_Overwrites_And_Deletes()
    {
        IEncryptedCredentialStore store = new InMemoryCredentialStore();

        Assert.Null(store.Get("AuthKey"));

        store.Set("AuthKey", "sagent_abc");
        Assert.Equal("sagent_abc", store.Get("AuthKey"));

        store.Set("AuthKey", "sagent_xyz"); // overwrite
        Assert.Equal("sagent_xyz", store.Get("AuthKey"));
        store.Set("DeviceKeyId", "device-key");

        store.DeleteMany(["AuthKey", "DeviceKeyId"]);
        Assert.Null(store.Get("AuthKey"));
        Assert.Null(store.Get("DeviceKeyId"));

        // Delete of an absent key is a no-op (does not throw).
        store.Delete("AuthKey");
    }

    [Fact]
    public void Dpapi_RoundTrips_PersistsAcrossInstances_AndEncryptsAtRest()
    {
        // DPAPI is Windows-only; the store is never instantiated elsewhere.
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"suavo_creds_{Guid.NewGuid():N}.dat");
        try
        {
            var store = new DpapiCredentialStore(path);
            store.Set("AuthKey", "sagent_secret_value");

            // New instance reads the same persisted file.
            var reopened = new DpapiCredentialStore(path);
            Assert.Equal("sagent_secret_value", reopened.Get("AuthKey"));

            // The on-disk file must NOT contain the plaintext.
            var raw = File.ReadAllText(path);
            Assert.DoesNotContain("sagent_secret_value", raw, StringComparison.Ordinal);

            reopened.Delete("AuthKey");
            Assert.Null(new DpapiCredentialStore(path).Get("AuthKey"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Dpapi_TamperFailsClosedAndIsNeverOverwrittenBySet()
    {
        if (!OperatingSystem.IsWindows())
            return;

        AssertDpapiTamperFailsClosedAndIsNeverOverwrittenBySet();
    }

    [SupportedOSPlatform("windows")]
    private static void AssertDpapiTamperFailsClosedAndIsNeverOverwrittenBySet()
    {
        var path = Path.Combine(Path.GetTempPath(), $"suavo_creds_{Guid.NewGuid():N}.dat");
        try
        {
            var store = new DpapiCredentialStore(path);
            store.Set(CredentialKeys.AuthKey, "sagent_original");
            File.WriteAllText(path, "{not-valid-json");

            Assert.Throws<InvalidDataException>(() => store.Get(CredentialKeys.AuthKey));
            Assert.Throws<InvalidDataException>(() => store.Set(CredentialKeys.AuthKey, "sagent_attacker"));
            Assert.Equal("{not-valid-json", File.ReadAllText(path));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Factory_Throws_On_NonWindows()
    {
        if (OperatingSystem.IsWindows())
            return; // On Windows the factory returns a DpapiCredentialStore.

        Assert.Throws<PlatformNotSupportedException>(() => CredentialStoreFactory.Create());
    }
}
