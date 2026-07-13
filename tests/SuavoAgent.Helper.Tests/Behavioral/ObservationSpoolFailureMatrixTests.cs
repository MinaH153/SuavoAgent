using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.Behavioral;
using Xunit;

namespace SuavoAgent.Helper.Tests.Behavioral;

/// <summary>
/// Covers persistence failures that must never be mistaken for an empty fresh
/// queue. The injected protection and access-control boundaries keep the tests
/// cross-platform while preserving production's ciphertext-only contract.
/// </summary>
public sealed class ObservationSpoolFailureMatrixTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-observation-spool-failure-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshSpoolWithoutPriorMarker_LoadsAsEmptyOnlyOnce()
    {
        using var spool = Create(new IdentityProtection());

        Assert.Null(spool.Load());
    }

    [Fact]
    public void BlankPathAndNullDependenciesAreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() =>
            new ObservationSpool(" ", new IdentityProtection(), new PermissiveAcl()));
        Assert.Throws<ArgumentNullException>(() =>
            new ObservationSpool(PathFor("null-protection"), null!, new PermissiveAcl()));
        Assert.Throws<ArgumentNullException>(() =>
            new ObservationSpool(PathFor("null-acl"), new IdentityProtection(), null!));
    }

    [Fact]
    public void ProductionFactoryRejectsNonWindowsInsteadOfUsingPlaintextFallback()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Throws<PlatformNotSupportedException>(() =>
            ObservationSpool.CreateProduction(BehavioralEventChannels.Pms));
    }

    [Fact]
    public void ConstructorAclFailureHasClosedStableCode()
    {
        var acl = new PermissiveAcl
        {
            PrepareFailure = new InvalidOperationException("untrusted directory"),
        };

        var error = Assert.Throws<BehavioralEventPersistenceException>(() =>
            new ObservationSpool(PathFor("acl-constructor"), new IdentityProtection(), acl));

        Assert.Equal("observation_spool_acl_invalid", error.Code);
    }

    [Fact]
    public void EmptyCiphertextFileIsNotAnEmptyQueue()
    {
        var path = PathFor("empty-ciphertext");
        using var spool = Create(new IdentityProtection(), path: path);
        File.WriteAllBytes(path, Array.Empty<byte>());

        var error = Assert.Throws<BehavioralEventPersistenceException>(() => spool.Load());

        Assert.Equal("observation_spool_size_invalid", error.Code);
    }

    [Fact]
    public void DecryptingToEmptyPlaintextFailsClosed()
    {
        var path = PathFor("empty-plaintext");
        using var spool = Create(new DelegateProtection(
            protect: bytes => bytes,
            unprotect: _ => Array.Empty<byte>()), path: path);
        File.WriteAllBytes(path, [1]);

        var error = Assert.Throws<BehavioralEventPersistenceException>(() => spool.Load());

        Assert.Equal("observation_spool_size_invalid", error.Code);
    }

    [Fact]
    public void InvalidDecryptedJsonFailsClosedAsCorrupt()
    {
        var path = PathFor("invalid-json");
        using var spool = Create(new IdentityProtection(), path: path);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("not-json"));

        var error = Assert.Throws<BehavioralEventPersistenceException>(() => spool.Load());

        Assert.Equal("observation_spool_corrupt", error.Code);
    }

    [Fact]
    public void NullDecryptedStateFailsClosedAsCorrupt()
    {
        var path = PathFor("null-json");
        using var spool = Create(new IdentityProtection(), path: path);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("null"));

        var error = Assert.Throws<BehavioralEventPersistenceException>(() => spool.Load());

        Assert.Equal("observation_spool_corrupt", error.Code);
    }

    [Fact]
    public void CryptographicUnprotectFailureGetsSpecificCode()
    {
        var path = PathFor("crypto-read");
        using var spool = Create(new DelegateProtection(
            protect: bytes => bytes,
            unprotect: _ => throw new CryptographicException()), path: path);
        File.WriteAllBytes(path, [1]);

        var error = Assert.Throws<BehavioralEventPersistenceException>(() => spool.Load());

        Assert.Equal("observation_spool_unprotect_failed", error.Code);
    }

    [Fact]
    public void UnexpectedUnprotectFailureGetsBoundedReadCode()
    {
        var path = PathFor("generic-read");
        using var spool = Create(new DelegateProtection(
            protect: bytes => bytes,
            unprotect: _ => throw new InvalidOperationException("internal detail")), path: path);
        File.WriteAllBytes(path, [1]);

        var error = Assert.Throws<BehavioralEventPersistenceException>(() => spool.Load());

        Assert.Equal("observation_spool_read_failed", error.Code);
        Assert.DoesNotContain("internal detail", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaveNullStateIsRejectedBeforeProtection()
    {
        var protection = new CountingProtection();
        using var spool = Create(protection);

        Assert.Throws<ArgumentNullException>(() => spool.Save(null!));
        Assert.Equal(0, protection.ProtectCalls);
    }

    [Fact]
    public void EmptyCiphertextFromProtectionFailsClosed()
    {
        using var spool = Create(new DelegateProtection(
            protect: _ => Array.Empty<byte>(),
            unprotect: bytes => bytes));

        var error = Assert.Throws<BehavioralEventPersistenceException>(() =>
            spool.Save(State()));

        Assert.Equal("observation_spool_size_invalid", error.Code);
    }

    [Fact]
    public void CryptographicProtectFailureGetsSpecificCode()
    {
        using var spool = Create(new DelegateProtection(
            protect: _ => throw new CryptographicException(),
            unprotect: bytes => bytes));

        var error = Assert.Throws<BehavioralEventPersistenceException>(() =>
            spool.Save(State()));

        Assert.Equal("observation_spool_protect_failed", error.Code);
    }

    [Fact]
    public void UnexpectedProtectFailureGetsBoundedWriteCode()
    {
        using var spool = Create(new DelegateProtection(
            protect: _ => throw new InvalidOperationException("secret internals"),
            unprotect: bytes => bytes));

        var error = Assert.Throws<BehavioralEventPersistenceException>(() =>
            spool.Save(State()));

        Assert.Equal("observation_spool_write_failed", error.Code);
        Assert.DoesNotContain("secret internals", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AclFailureAfterTempWriteDeletesTemporaryCiphertext()
    {
        var path = PathFor("temp-cleanup");
        var acl = new PermissiveAcl { FailProtectCall = 2 };
        using var spool = Create(new IdentityProtection(), acl, path);

        var error = Assert.Throws<BehavioralEventPersistenceException>(() =>
            spool.Save(State()));

        Assert.Equal("observation_spool_write_failed", error.Code);
        Assert.Empty(Directory.EnumerateFiles(
            Path.GetDirectoryName(path)!,
            ".temp-cleanup.spool.*.tmp"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void StaleTemporaryFileMustPassAclValidationBeforeDeletion()
    {
        var path = PathFor("stale-validation");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stale = Path.Combine(
            Path.GetDirectoryName(path)!,
            ".stale-validation.spool.attacker.tmp");
        File.WriteAllBytes(stale, [1]);
        var acl = new PermissiveAcl { RejectPath = stale };

        var error = Assert.Throws<BehavioralEventPersistenceException>(() =>
            new ObservationSpool(path, new IdentityProtection(), acl));

        Assert.Equal("observation_spool_acl_invalid", error.Code);
        Assert.True(File.Exists(stale));
    }

    [Fact]
    public void LoadAndSaveAfterDisposeAreRejected()
    {
        var spool = Create(new IdentityProtection());
        spool.Dispose();
        spool.Dispose();

        Assert.Throws<ObjectDisposedException>(() => spool.Load());
        Assert.Throws<ObjectDisposedException>(() => spool.Save(State()));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private ObservationSpool Create(
        IObservationSpoolProtection protection,
        IObservationSpoolAccessControl? accessControl = null,
        string? path = null) => new(
            path ?? PathFor(Guid.NewGuid().ToString("N")),
            protection,
            accessControl ?? new PermissiveAcl());

    private string PathFor(string name) => Path.Combine(_root, name + ".spool");

    private static BehavioralEventBufferState State() => new()
    {
        StreamId = Guid.NewGuid().ToString("N"),
        Channel = BehavioralEventChannels.System,
        QueuedEvents = [BehavioralEvent.ObserverStatus("test", "ready")],
        LastAssignedSequence = 1,
    };

    private sealed class IdentityProtection : IObservationSpoolProtection
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray();
    }

    private sealed class CountingProtection : IObservationSpoolProtection
    {
        public int ProtectCalls { get; private set; }

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            ProtectCalls++;
            return plaintext.ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray();
    }

    private sealed class DelegateProtection(
        Func<byte[], byte[]> protect,
        Func<byte[], byte[]> unprotect) : IObservationSpoolProtection
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext) => protect(plaintext.ToArray());
        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => unprotect(ciphertext.ToArray());
    }

    private sealed class PermissiveAcl : IObservationSpoolAccessControl
    {
        private int _protectCalls;

        public Exception? PrepareFailure { get; init; }
        public int? FailProtectCall { get; init; }
        public string? RejectPath { get; init; }

        public void PrepareAndValidateDirectory(string directory)
        {
            if (PrepareFailure is not null)
                throw PrepareFailure;
            Directory.CreateDirectory(directory);
        }

        public void ProtectAndValidateFile(string path)
        {
            _protectCalls++;
            if (_protectCalls == FailProtectCall)
                throw new InvalidOperationException("ACL application failed");
            ValidateFile(path);
        }

        public void ValidateDirectory(string directory)
        {
            if (string.Equals(directory, RejectPath, StringComparison.Ordinal))
                throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
        }

        public void ValidateFile(string path)
        {
            if (string.Equals(path, RejectPath, StringComparison.Ordinal))
                throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
        }
    }
}
