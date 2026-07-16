using System.Security.AccessControl;
using System.Text;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.InstallerSupport;
using Xunit;

namespace SuavoAgent.Setup.Tests.InstallerSupport;

public sealed class FileInstallerServiceHardeningJournalTests : IDisposable
{
    private static readonly string InvocationId = new('a', 64);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-msi-hardening-journal-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Save_and_load_round_trip_exact_bounded_snapshot_after_security_callback()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "rollback.json");
        var secured = false;
        var journal = new FileInstallerServiceHardeningJournal(
            path,
            candidate =>
            {
                Assert.Equal(path, candidate);
                Assert.True(File.Exists(candidate));
                secured = true;
            });
        var snapshots = Snapshots();

        journal.SavePending(InvocationId, snapshots);
        var loaded = journal.Load();

        Assert.True(secured);
        Assert.NotNull(loaded);
        Assert.Equal(InvocationId, loaded?.InvocationId);
        Assert.Equal(InstallerTransactionJournalPhase.Pending, loaded?.Phase);
        Assert.Equal(snapshots, loaded?.Snapshots);
        Assert.InRange(new FileInfo(path).Length, 1, FileInstallerServiceHardeningJournal.MaximumBytes);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_refuses_to_replace_existing_journal()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "rollback.json");
        var journal = new FileInstallerServiceHardeningJournal(path, static _ => { });
        journal.SavePending(InvocationId, Snapshots());
        var original = File.ReadAllBytes(path);

        Assert.Throws<IOException>(() => journal.SavePending(InvocationId, Snapshots()));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Save_removes_journal_when_exact_acl_application_fails()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "rollback.json");
        var journal = new FileInstallerServiceHardeningJournal(
            path,
            static _ => throw new InvalidDataException("Injected reparse rejection."));

        Assert.Throws<InvalidDataException>(() =>
            journal.SavePending(InvocationId, Snapshots()));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Load_rejects_oversized_or_unknown_schema_without_deleting_evidence()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "rollback.json");
        var journal = new FileInstallerServiceHardeningJournal(path, static _ => { });
        File.WriteAllBytes(
            path,
            Enumerable.Repeat((byte)'x', FileInstallerServiceHardeningJournal.MaximumBytes + 1)
                .ToArray());

        Assert.Throws<InvalidDataException>(() => journal.Load());
        Assert.True(File.Exists(path));

        File.WriteAllText(
            path,
            "{\"schemaVersion\":2,\"invocationId\":\"" + InvocationId +
            "\",\"phase\":\"pending\",\"services\":[],\"unexpected\":true}",
            new UTF8Encoding(false));
        Assert.ThrowsAny<Exception>(() => journal.Load());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Commit_phase_is_durable_and_delete_requires_exact_identity_and_phase()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "rollback.json");
        var journal = new FileInstallerServiceHardeningJournal(path, static _ => { });
        journal.SavePending(InvocationId, Snapshots());

        journal.MarkCommitted(InvocationId);

        Assert.Equal(
            InstallerTransactionJournalPhase.Committed,
            journal.Load()?.Phase);
        Assert.Throws<InvalidDataException>(() => journal.Delete(
            new string('b', 64),
            InstallerTransactionJournalPhase.Committed));
        Assert.Throws<InvalidDataException>(() => journal.Delete(
            InvocationId,
            InstallerTransactionJournalPhase.Pending));
        Assert.True(File.Exists(path));

        journal.Delete(InvocationId, InstallerTransactionJournalPhase.Committed);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Journal_acl_is_exact_system_and_administrators_only()
    {
        var policy = FileInstallerServiceHardeningJournal.BuildJournalAclPolicy();

        Assert.Equal(HandleBoundAcl.SystemSid, policy.OwnerSid);
        Assert.Equal(
            [HandleBoundAcl.SystemSid, HandleBoundAcl.AdministratorsSid],
            policy.Aces.Select(static ace => ace.Sid));
        Assert.All(policy.Aces, ace =>
        {
            Assert.Equal(FileSystemRights.FullControl, ace.Rights);
            Assert.Equal(InheritanceFlags.None, ace.InheritanceFlags);
        });
        Assert.DoesNotContain(policy.Aces, ace => ace.Sid == HandleBoundAcl.UsersSid);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private static IReadOnlyDictionary<string, InstallerServiceConfiguration>
        Snapshots() =>
        MsiServiceHardeningTransaction.ServiceNames.ToDictionary(
            static name => name,
            name => new InstallerServiceConfiguration(
                DelayedAutoStart: name != "SuavoAgent.Core",
                ServiceSidType: name == "SuavoAgent.Watchdog" ? 3u : 0u),
            StringComparer.Ordinal);
}
