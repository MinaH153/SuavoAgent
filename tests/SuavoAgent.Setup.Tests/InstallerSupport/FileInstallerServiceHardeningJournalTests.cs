using System.Security.AccessControl;
using System.Text;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.InstallerSupport;
using Xunit;

namespace SuavoAgent.Setup.Tests.InstallerSupport;

public sealed class FileInstallerServiceHardeningJournalTests : IDisposable
{
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

        journal.Save(snapshots);
        var loaded = journal.Load();

        Assert.True(secured);
        Assert.NotNull(loaded);
        Assert.Equal(snapshots, loaded);
        Assert.InRange(new FileInfo(path).Length, 1, FileInstallerServiceHardeningJournal.MaximumBytes);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_refuses_to_replace_existing_journal()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "rollback.json");
        var journal = new FileInstallerServiceHardeningJournal(path, static _ => { });
        journal.Save(Snapshots());
        var original = File.ReadAllBytes(path);

        Assert.Throws<IOException>(() => journal.Save(Snapshots()));
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

        Assert.Throws<InvalidDataException>(() => journal.Save(Snapshots()));
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
            "{\"schemaVersion\":1,\"services\":[],\"unexpected\":true}",
            new UTF8Encoding(false));
        Assert.ThrowsAny<Exception>(() => journal.Load());
        Assert.True(File.Exists(path));
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
