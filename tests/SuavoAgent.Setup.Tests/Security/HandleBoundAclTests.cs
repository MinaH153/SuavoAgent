using System.Security.AccessControl;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Security;
using Xunit;

namespace SuavoAgent.Setup.Tests.Security;

public sealed class HandleBoundAclTests
{
    [Fact]
    public void Service_installer_acl_paths_have_no_shell_or_path_based_security_mutation()
    {
        var repository = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var setup = Path.Combine(repository, "src", "SuavoAgent.Setup");
        var contracts = Path.Combine(repository, "src", "SuavoAgent.Contracts");
        var diagnostics = Path.Combine(repository, "src", "SuavoAgent.Diagnostics");
        var broker = Path.Combine(repository, "src", "SuavoAgent.Broker");
        var core = Path.Combine(repository, "src", "SuavoAgent.Core");
        var sources = new[]
        {
            Path.Combine(setup, "ServiceInstaller.Acl.cs"),
            Path.Combine(setup, "ServiceInstaller.Uninstall.cs"),
            Path.Combine(setup, "Security", "SqlServerCertificateEnrollment.cs"),
            Path.Combine(diagnostics, "HelperExeAclGrant.cs"),
            Path.Combine(
                diagnostics,
                "Maintenance",
                "PrivilegedExecutableStaging.cs"),
            Path.Combine(broker, "Honeytoken", "EtwHoneytokenFileTracer.cs"),
            Path.Combine(contracts, "Reasoning", "BrainCohortAcl.cs"),
            Path.Combine(
                contracts,
                "Security",
                "PioneerRxApprovalMetadataAcl.cs"),
            Path.Combine(core, "State", "InstalledDataRootVerifier.cs"),
            Path.Combine(contracts, "Security", "HandleBoundAcl.cs"),
        };
        if (sources.Any(path => !File.Exists(path))) return;
        var mutationSources = string.Join(
            Environment.NewLine,
            sources.Take(sources.Length - 1).Select(File.ReadAllText));
        var engineSource = File.ReadAllText(sources[^1]);

        Assert.DoesNotContain("icacls", mutationSources, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SetAccessControl", mutationSources, StringComparison.Ordinal);
        Assert.Contains("SetSecurityInfo", engineSource, StringComparison.Ordinal);
        Assert.Contains("FileFlagOpenReparsePoint", engineSource, StringComparison.Ordinal);
        Assert.Contains("GetFileInformationByHandle", engineSource, StringComparison.Ordinal);
        Assert.Contains("GetFinalPathNameByHandleW", engineSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_open_contract_is_no_follow_and_never_shares_delete()
    {
        Assert.Equal(
            Win32HandleBoundAclNative.FileShareRead,
            Win32HandleBoundAclNative.OpenShareMode);
        Assert.Equal(0u, Win32HandleBoundAclNative.OpenShareMode &
                         Win32HandleBoundAclNative.FileShareWrite);
        Assert.Equal(0u, Win32HandleBoundAclNative.OpenShareMode & 0x00000004u);
        Assert.Equal(
            Win32HandleBoundAclNative.FileFlagBackupSemantics |
            Win32HandleBoundAclNative.FileFlagOpenReparsePoint,
            Win32HandleBoundAclNative.OpenFlags);
    }

    [Fact]
    public void Preplanted_reparse_root_is_rejected_without_any_acl_mutation()
    {
        var root = Absolute("reparse-root");
        var native = new FakeNative();
        native.Add(root, Identity(root, directory: true, reparse: true));

        var error = Assert.Throws<InvalidDataException>(() =>
            ApplyBaseTree(
                new HandleBoundAcl(native),
                root,
                ServiceInstaller.ProtectedDirectoryKind.Install));

        Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(native.Mutations);
        Assert.DoesNotContain(native.Events, item => item.StartsWith("enumerate:", StringComparison.Ordinal));
    }

    [Fact]
    public void Preplanted_reparse_child_is_never_mutated_or_followed()
    {
        var root = Absolute("reparse-child");
        var link = Path.Combine(root, "legacy-junction");
        var native = new FakeNative();
        native.Add(root, Identity(root, directory: true));
        native.Add(link, Identity(link, directory: true, reparse: true));
        native.Children[root] = [link];

        Assert.Throws<InvalidDataException>(() =>
            ApplyBaseTree(
                new HandleBoundAcl(native),
                root,
                ServiceInstaller.ProtectedDirectoryKind.Data));

        Assert.Single(native.Mutations); // root's non-inheriting barrier only
        Assert.Equal(root, native.Mutations[0].Path);
        Assert.DoesNotContain(native.Mutations, mutation =>
            string.Equals(mutation.Path, link, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(native.Opened, path => path.Contains("target", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Batch_prevalidation_rejects_one_reparse_before_mutating_any_safe_peer()
    {
        var root = Absolute("batch-root");
        var link = Absolute("batch-link");
        var native = new FakeNative();
        native.Add(root, Identity(root, directory: true));
        native.Add(link, Identity(link, directory: false, reparse: true));
        var policy = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Maintenance,
            directory: true,
            inherit: true);

        Assert.Throws<InvalidDataException>(() => new HandleBoundAcl(native).ApplyBatch(
        [
            new(root, true, policy),
            new(
                link,
                false,
                ServiceInstaller.BuildProtectedAclPolicy(
                    ServiceInstaller.ProtectedDirectoryKind.Maintenance,
                    directory: false,
                    inherit: false)),
        ]));

        Assert.Empty(native.Mutations);
    }

    [Fact]
    public void Read_side_batch_verification_accepts_only_exact_acl_without_repair()
    {
        var path = Absolute("verify-exact");
        var policy = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Maintenance,
            directory: false,
            inherit: false);
        var native = new FakeNative();
        native.Add(path, Identity(path, directory: false));
        native.SetSnapshot(path, new(
            HandleBoundAcl.SystemSid,
            IsDaclProtected: true,
            policy.Aces));

        new HandleBoundAcl(native).VerifyBatch([new(path, false, policy)]);

        Assert.Empty(native.Mutations);
    }

    [Fact]
    public void Read_side_batch_verification_rejects_extra_writer_without_repair()
    {
        var path = Absolute("verify-forged-acl");
        var policy = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Maintenance,
            directory: false,
            inherit: false);
        var native = new FakeNative();
        native.Add(path, Identity(path, directory: false));
        native.SetSnapshot(path, new(
            HandleBoundAcl.SystemSid,
            IsDaclProtected: true,
            policy.Aces.Append(new HandleBoundAclAce(
                CoreServiceIdentity.ServiceSid,
                FileSystemRights.Modify)).ToArray()));

        Assert.Throws<UnauthorizedAccessException>(() =>
            new HandleBoundAcl(native).VerifyBatch([new(path, false, policy)]));

        Assert.Empty(native.Mutations);
    }

    [Fact]
    public void Handle_identity_mismatch_aborts_before_set_security_info()
    {
        var path = Absolute("identity-swap");
        var first = Identity(path, directory: false, fileId: 41);
        var swapped = first with { FileId = 42 };
        var native = new FakeNative();
        native.Add(path, first, swapped);

        Assert.Throws<InvalidDataException>(() => new HandleBoundAcl(native).ApplyBatch(
        [
            new(
                path,
                false,
                ServiceInstaller.BuildProtectedAclPolicy(
                    ServiceInstaller.ProtectedDirectoryKind.Install,
                    directory: false,
                    inherit: false)),
        ]));

        Assert.Empty(native.Mutations);
    }

    [Fact]
    public void Multiply_linked_file_is_rejected_before_acl_mutation()
    {
        var path = Absolute("hard-link");
        var native = new FakeNative();
        native.Add(path, Identity(path, directory: false) with { NumberOfLinks = 2 });

        Assert.Throws<InvalidDataException>(() => new HandleBoundAcl(native).ApplyBatch(
        [
            new(
                path,
                false,
                ServiceInstaller.BuildProtectedAclPolicy(
                    ServiceInstaller.ProtectedDirectoryKind.Install,
                    directory: false,
                    inherit: false)),
        ]));

        Assert.Empty(native.Mutations);
    }

    [Fact]
    public void Exact_policy_requires_system_owner_protected_dacl_and_only_expected_aces()
    {
        var inherited = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var policy = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Data,
            directory: true,
            inherit: true,
            new HandleBoundAclAce(
                HandleBoundAcl.UsersSid,
                FileSystemRights.ReadAndExecute));
        var exact = new HandleBoundAclSnapshot(
            HandleBoundAcl.SystemSid,
            IsDaclProtected: true,
            policy.Aces);

        Assert.True(HandleBoundAcl.IsExact(exact, policy));
        Assert.False(HandleBoundAcl.IsExact(
            exact with { OwnerSid = HandleBoundAcl.AdministratorsSid },
            policy));
        Assert.False(HandleBoundAcl.IsExact(exact with { IsDaclProtected = false }, policy));
        Assert.False(HandleBoundAcl.IsExact(
            exact with
            {
                Aces = policy.Aces.Append(new HandleBoundAclAce(
                    "S-1-1-0",
                    FileSystemRights.Read)).ToArray(),
            },
            policy));
        Assert.Contains(policy.Aces, ace =>
            ace.Sid == HandleBoundAcl.SystemSid &&
            ace.Rights == FileSystemRights.FullControl &&
            ace.InheritanceFlags == inherited);
        Assert.Contains(policy.Aces, ace =>
            ace.Sid == HandleBoundAcl.UsersSid &&
            ace.Rights == FileSystemRights.ReadAndExecute &&
            ace.InheritanceFlags == InheritanceFlags.None);
    }

    [Fact]
    public void Directory_barrier_cannot_grant_more_authority_than_final_policy()
    {
        var root = Absolute("broad-barrier");
        var directory = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Maintenance,
            directory: true,
            inherit: true);
        var file = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Maintenance,
            directory: false,
            inherit: false);
        var broadBarrier = ServiceInstaller.BuildProtectedAclPolicy(
            ServiceInstaller.ProtectedDirectoryKind.Maintenance,
            directory: true,
            inherit: false,
            new HandleBoundAclAce("S-1-1-0", FileSystemRights.FullControl));
        var native = new FakeNative();

        Assert.Throws<InvalidDataException>(() => new HandleBoundAcl(native).ApplyTree(
            root,
            directory,
            file,
            broadBarrier));
        Assert.Empty(native.Opened);
        Assert.Empty(native.Mutations);
    }

    [Fact]
    public void Directory_barrier_is_root_first_and_non_inheriting()
    {
        var root = Absolute("root-first");
        var child = Path.Combine(root, "child.bin");
        var native = new FakeNative();
        native.Add(root, Identity(root, directory: true));
        native.Add(child, Identity(child, directory: false));
        native.Children[root] = [child];

        ApplyBaseTree(
            new HandleBoundAcl(native),
            root,
            ServiceInstaller.ProtectedDirectoryKind.Install);

        Assert.Equal(3, native.Mutations.Count);
        Assert.Equal(root, native.Mutations[0].Path);
        Assert.All(native.Mutations[0].Policy.Aces, ace =>
            Assert.Equal(InheritanceFlags.None, ace.InheritanceFlags));
        Assert.Equal(child, native.Mutations[1].Path);
        Assert.Equal(root, native.Mutations[2].Path);
        Assert.All(native.Mutations[2].Policy.Aces, ace => Assert.Equal(
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            ace.InheritanceFlags));
    }

    [Fact]
    public void Writable_helper_subtree_updates_existing_protected_children_explicitly()
    {
        var root = Absolute("helper-subtree");
        var file = Path.Combine(root, "existing.log");
        var directory = Path.Combine(root, "archive");
        var nested = Path.Combine(directory, "existing.jsonl");
        var native = new FakeNative();
        native.Add(root, Identity(root, directory: true));
        native.Add(file, Identity(file, directory: false, fileId: 8));
        native.Add(directory, Identity(directory, directory: true, fileId: 9));
        native.Add(nested, Identity(nested, directory: false, fileId: 10));
        native.Children[root] = [file, directory];
        native.Children[directory] = [nested];

        new HandleBoundAcl(native).ApplyTree(
            root,
            ServiceInstaller.BuildProtectedAclPolicy(
                ServiceInstaller.ProtectedDirectoryKind.Data,
                directory: true,
                inherit: true,
                new HandleBoundAclAce(
                    HandleBoundAcl.UsersSid,
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)),
            ServiceInstaller.BuildProtectedAclPolicy(
                ServiceInstaller.ProtectedDirectoryKind.Data,
                directory: false,
                inherit: false,
                new HandleBoundAclAce(
                    HandleBoundAcl.UsersSid,
                    FileSystemRights.Modify)),
            ServiceInstaller.BuildProtectedAclPolicy(
                ServiceInstaller.ProtectedDirectoryKind.Data,
                directory: true,
                inherit: false));

        foreach (var path in new[] { root, file, directory, nested })
        {
            var final = native.Mutations.Last(mutation =>
                string.Equals(mutation.Path, path, StringComparison.OrdinalIgnoreCase));
            var users = Assert.Single(final.Policy.Aces, ace =>
                ace.Sid == HandleBoundAcl.UsersSid);
            Assert.Equal(FileSystemRights.Modify, users.Rights);
            Assert.Equal(
                path == file || path == nested
                    ? InheritanceFlags.None
                    : InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                users.InheritanceFlags);
        }
    }

    private static string Absolute(string name) =>
        Path.Combine(Path.GetTempPath(), "suavo-acl-tests", name);

    private static void ApplyBaseTree(
        HandleBoundAcl acl,
        string root,
        ServiceInstaller.ProtectedDirectoryKind kind) => acl.ApplyTree(
        root,
        ServiceInstaller.BuildProtectedAclPolicy(kind, directory: true, inherit: true),
        ServiceInstaller.BuildProtectedAclPolicy(kind, directory: false, inherit: false),
        ServiceInstaller.BuildProtectedAclPolicy(kind, directory: true, inherit: false));

    private static HandleBoundObjectIdentity Identity(
        string path,
        bool directory,
        bool reparse = false,
        ulong fileId = 7) => new(
        HandleBoundAcl.NormalizeExpectedPath(path),
        directory,
        reparse,
        VolumeSerialNumber: 19,
        FileId: fileId,
        NumberOfLinks: 1);

    private sealed class FakeNative : IHandleBoundAclNative
    {
        private readonly Dictionary<string, FakeObject> _objects =
            new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, IReadOnlyList<string>> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal List<string> Opened { get; } = [];
        internal List<string> Events { get; } = [];
        internal List<(string Path, HandleBoundAclPolicy Policy)> Mutations { get; } = [];

        internal void Add(string path, params HandleBoundObjectIdentity[] identities)
        {
            var normalized = HandleBoundAcl.NormalizeExpectedPath(path);
            _objects.Add(normalized, new(normalized, identities));
        }

        internal void SetSnapshot(string path, HandleBoundAclSnapshot snapshot)
        {
            var normalized = HandleBoundAcl.NormalizeExpectedPath(path);
            _objects[normalized].Snapshot = snapshot;
        }

        public IDisposable EnableRequiredPrivileges() => new NoopDisposable();

        public IHandleBoundAclObject OpenNoFollow(string path)
        {
            var normalized = HandleBoundAcl.NormalizeExpectedPath(path);
            Opened.Add(normalized);
            Events.Add("open:" + normalized);
            return _objects.TryGetValue(normalized, out var value)
                ? new FakeHandle(value)
                : throw new FileNotFoundException("Fake ACL object missing.", normalized);
        }

        public HandleBoundObjectIdentity ReadIdentity(IHandleBoundAclObject handle) =>
            Require(handle).Object.ReadIdentity();

        public IReadOnlyList<string> EnumerateChildren(string canonicalDirectoryPath)
        {
            var normalized = HandleBoundAcl.NormalizeExpectedPath(canonicalDirectoryPath);
            Events.Add("enumerate:" + normalized);
            return Children.TryGetValue(normalized, out var children) ? children : [];
        }

        public void SetExactSecurity(
            IHandleBoundAclObject handle,
            HandleBoundAclPolicy policy)
        {
            var value = Require(handle).Object;
            Mutations.Add((value.Path, policy));
            Events.Add("mutate:" + value.Path);
            value.Snapshot = new(
                policy.OwnerSid,
                IsDaclProtected: true,
                policy.Aces.ToArray());
        }

        public HandleBoundAclSnapshot ReadSecurity(IHandleBoundAclObject handle) =>
            Require(handle).Object.Snapshot ??
            throw new InvalidOperationException("Fake ACL was not set.");

        private static FakeHandle Require(IHandleBoundAclObject handle) =>
            handle as FakeHandle ?? throw new ArgumentException("Unexpected fake handle.");

        private sealed class FakeHandle(FakeObject value) : IHandleBoundAclObject
        {
            internal FakeObject Object { get; } = value;
            public void Dispose() { }
        }

        private sealed class FakeObject(
            string path,
            IReadOnlyList<HandleBoundObjectIdentity> identities)
        {
            private int _reads;
            internal string Path { get; } = path;
            internal HandleBoundAclSnapshot? Snapshot { get; set; }

            internal HandleBoundObjectIdentity ReadIdentity()
            {
                if (identities.Count == 0)
                    throw new InvalidOperationException("Fake identity sequence is empty.");
                var index = Math.Min(_reads++, identities.Count - 1);
                return identities[index];
            }
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }
}
