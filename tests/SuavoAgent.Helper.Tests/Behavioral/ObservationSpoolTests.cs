using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.Behavioral;
using Xunit;

namespace SuavoAgent.Helper.Tests.Behavioral;

public sealed class ObservationSpoolTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-observation-spool-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void InjectedProtectionAndAcl_RoundTripOnMacWithoutPlaintextAtRest()
    {
        var path = Path.Combine(_root, "pms.spool");
        var acl = new FakeAccessControl();
        using var spool = new ObservationSpool(path, new AuthenticatedTestProtection(), acl);
        var state = State("secret-tree-material", "secret-lease-key-material");

        spool.Save(state);
        var raw = File.ReadAllBytes(path);
        var rawText = Encoding.UTF8.GetString(raw);
        var loaded = spool.Load();

        Assert.NotNull(loaded);
        Assert.Equal(state.StreamId, loaded!.StreamId);
        Assert.Equal("secret-tree-material", loaded.QueuedEvents.Single().TreeHash);
        Assert.DoesNotContain("secret-tree-material", rawText, StringComparison.Ordinal);
        Assert.DoesNotContain(state.ActiveLease!.KeyMaterial, rawText, StringComparison.Ordinal);
        Assert.True(acl.DirectoryPrepareCount >= 1);
        Assert.True(acl.FileProtectCount >= 2); // lock + atomic spool temp
    }

    [Fact]
    public void CorruptCiphertext_FailsClosedWithNoPartialState()
    {
        var path = Path.Combine(_root, "system.spool");
        using var spool = new ObservationSpool(
            path,
            new AuthenticatedTestProtection(),
            new FakeAccessControl());
        spool.Save(State("tree", "key"));
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x5A;
        File.WriteAllBytes(path, bytes);

        var error = Assert.Throws<BehavioralEventPersistenceException>(() => spool.Load());

        Assert.Equal("observation_spool_unprotect_failed", error.Code);
    }

    [Fact]
    public void AclValidationFailure_FailsClosedBeforeDecrypt()
    {
        var path = Path.Combine(_root, "pms.spool");
        var acl = new FakeAccessControl();
        using var spool = new ObservationSpool(path, new AuthenticatedTestProtection(), acl);
        spool.Save(State("tree", "key"));
        acl.RejectValidation = true;

        var error = Assert.Throws<BehavioralEventPersistenceException>(() => spool.Load());

        Assert.Equal("observation_spool_acl_invalid", error.Code);
    }

    [Fact]
    public void ConcurrentHelperCannotOpenSameSpool()
    {
        var path = Path.Combine(_root, "pms.spool");
        using var first = new ObservationSpool(
            path,
            new AuthenticatedTestProtection(),
            new FakeAccessControl());

        var error = Assert.Throws<BehavioralEventPersistenceException>(() =>
            new ObservationSpool(
                path,
                new AuthenticatedTestProtection(),
                new FakeAccessControl()));

        Assert.Equal("observation_spool_locked", error.Code);
    }

    [Fact]
    public void PriorInitializationMarkerWithoutSpool_IsTreatedAsLossNotFreshInstall()
    {
        var path = Path.Combine(_root, "pms.spool");
        using (var first = new ObservationSpool(
                   path,
                   new AuthenticatedTestProtection(),
                   new FakeAccessControl()))
        {
            // Simulate termination after lock/ACL initialization but before
            // the buffer could persist its initial stream state.
        }

        using var restarted = new ObservationSpool(
            path,
            new AuthenticatedTestProtection(),
            new FakeAccessControl());
        var error = Assert.Throws<BehavioralEventPersistenceException>(() => restarted.Load());

        Assert.Equal("observation_spool_missing", error.Code);
    }

    [Fact]
    public void Restart_RemovesOnlyAclValidatedStaleCiphertextTemporaryFiles()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "pms.spool");
        var stale = Path.Combine(_root, ".pms.spool.deadbeef.tmp");
        File.WriteAllBytes(stale, [1, 2, 3]);

        using var spool = new ObservationSpool(
            path,
            new AuthenticatedTestProtection(),
            new FakeAccessControl());

        Assert.False(File.Exists(stale));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { }
    }

    private static BehavioralEventBufferState State(string treeHash, string keyMarker)
    {
        var leaseKey = Convert.ToBase64String(
            SHA256.HashData(Encoding.UTF8.GetBytes(keyMarker)));
        return new BehavioralEventBufferState
        {
            StreamId = Guid.NewGuid().ToString("N"),
            Channel = BehavioralEventChannels.Pms,
            LastAssignedSequence = 1,
            ActiveLease = new ObservationKeyLease
            {
                LeaseId = "opaque-lease",
                SessionBinding = "opaque-session",
                Epoch = 1,
                IssuedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(15),
                KeyMaterial = leaseKey,
            },
            QueuedEvents = [BehavioralEvent.TreeSnapshot(treeHash).WithSeq(1)],
        };
    }

    private sealed class FakeAccessControl : IObservationSpoolAccessControl
    {
        public int DirectoryPrepareCount { get; private set; }
        public int FileProtectCount { get; private set; }
        public bool RejectValidation { get; set; }

        public void PrepareAndValidateDirectory(string directory)
        {
            Directory.CreateDirectory(directory);
            DirectoryPrepareCount++;
            ValidateDirectory(directory);
        }

        public void ProtectAndValidateFile(string path)
        {
            FileProtectCount++;
            ValidateFile(path);
        }

        public void ValidateDirectory(string directory)
        {
            if (RejectValidation)
                throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
        }

        public void ValidateFile(string path)
        {
            if (RejectValidation)
                throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
        }
    }

    private sealed class AuthenticatedTestProtection : IObservationSpoolProtection
    {
        private static readonly byte[] Key = SHA256.HashData(
            Encoding.UTF8.GetBytes("observation-spool-test-key"));

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            var ciphertext = new byte[plaintext.Length];
            for (var index = 0; index < plaintext.Length; index++)
                ciphertext[index] = (byte)(plaintext[index] ^ Key[index % Key.Length]);
            var tag = HMACSHA256.HashData(Key, ciphertext);
            return tag.Concat(ciphertext).ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
        {
            if (protectedBytes.Length <= 32)
                throw new CryptographicException();
            var tag = protectedBytes[..32];
            var ciphertext = protectedBytes[32..];
            var expected = HMACSHA256.HashData(Key, ciphertext);
            if (!CryptographicOperations.FixedTimeEquals(tag, expected))
                throw new CryptographicException();

            var plaintext = new byte[ciphertext.Length];
            for (var index = 0; index < ciphertext.Length; index++)
                plaintext[index] = (byte)(ciphertext[index] ^ Key[index % Key.Length]);
            return plaintext;
        }
    }
}
