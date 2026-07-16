using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace SuavoAgent.Contracts.Security;

/// <summary>
/// A purpose-specific ACE in an exact protected DACL. The inheritance flags are
/// explicit because directory roots, traverse-only carve-outs, and writable
/// Helper subtrees deliberately have different propagation boundaries.
/// </summary>
public sealed record HandleBoundAclAce(
    string Sid,
    FileSystemRights Rights,
    InheritanceFlags InheritanceFlags = InheritanceFlags.None);

/// <summary>An exact owner + protected DACL written through one validated handle.</summary>
public sealed record HandleBoundAclPolicy(
    string OwnerSid,
    IReadOnlyList<HandleBoundAclAce> Aces);

internal sealed record HandleBoundAclSnapshot(
    string OwnerSid,
    bool IsDaclProtected,
    IReadOnlyList<HandleBoundAclAce> Aces);

/// <summary>
/// Stable identity obtained from BY_HANDLE_FILE_INFORMATION plus the canonical
/// name returned for that same handle. No lexical path is accepted as identity.
/// </summary>
internal sealed record HandleBoundObjectIdentity(
    string CanonicalPath,
    bool IsDirectory,
    bool IsReparsePoint,
    uint VolumeSerialNumber,
    ulong FileId,
    uint NumberOfLinks)
{
    internal bool IsSameObject(HandleBoundObjectIdentity other) =>
        string.Equals(CanonicalPath, other.CanonicalPath, StringComparison.OrdinalIgnoreCase) &&
        IsDirectory == other.IsDirectory &&
        IsReparsePoint == other.IsReparsePoint &&
        VolumeSerialNumber == other.VolumeSerialNumber &&
        FileId == other.FileId &&
        NumberOfLinks == other.NumberOfLinks;
}

internal interface IHandleBoundAclObject : IDisposable;

internal interface IHandleBoundAclNative
{
    IDisposable EnableRequiredPrivileges();
    IHandleBoundAclObject OpenNoFollow(string path);
    HandleBoundObjectIdentity ReadIdentity(IHandleBoundAclObject handle);
    IReadOnlyList<string> EnumerateChildren(string canonicalDirectoryPath);
    void SetExactSecurity(IHandleBoundAclObject handle, HandleBoundAclPolicy policy);
    HandleBoundAclSnapshot ReadSecurity(IHandleBoundAclObject handle);
}

public sealed record HandleBoundAclMutation(
    string Path,
    bool IsDirectory,
    HandleBoundAclPolicy Policy);

/// <summary>
/// Applies privileged filesystem ACLs without ever reopening a path for the
/// security mutation. Each object is opened with OPEN_REPARSE_POINT, no delete
/// sharing, and directory backup semantics; type, file ID, and canonical path
/// are rechecked immediately before and after SetSecurityInfo on that handle.
/// </summary>
public sealed class HandleBoundAcl
{
    public const int MaximumTreeEntries = 50_000;
    public const int MaximumTreeDepth = 256;
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string UsersSid = "S-1-5-32-545";

    private readonly IHandleBoundAclNative _native;

    public HandleBoundAcl() : this(new Win32HandleBoundAclNative()) { }

    internal HandleBoundAcl(IHandleBoundAclNative native) =>
        _native = native ?? throw new ArgumentNullException(nameof(native));

    /// <summary>
    /// Locks an existing tree. Each directory first receives a non-inheriting
    /// exact barrier, so a preplanted child reparse point is rejected before an
    /// inheritable ACE can be propagated to it. After every child is validated
    /// and protected, the directory receives its final inheritable DACL.
    /// </summary>
    public void ApplyTree(
        string rootPath,
        HandleBoundAclPolicy directoryPolicy,
        HandleBoundAclPolicy filePolicy,
        HandleBoundAclPolicy? barrierDirectoryPolicy = null,
        int maximumEntries = MaximumTreeEntries,
        int maximumDepth = MaximumTreeDepth)
    {
        ValidateTreeArguments(
            directoryPolicy,
            filePolicy,
            barrierDirectoryPolicy,
            maximumEntries,
            maximumDepth);
        using var privileges = _native.EnableRequiredPrivileges();
        var expectedRoot = NormalizeExpectedPath(rootPath);
        using var root = _native.OpenNoFollow(expectedRoot);
        var count = 1;
        ProtectDirectory(
            root,
            expectedRoot,
            directoryPolicy,
            filePolicy,
            barrierDirectoryPolicy ?? WithoutInheritance(directoryPolicy),
            maximumEntries,
            maximumDepth,
            depth: 0,
            ref count);
    }

    /// <summary>
    /// Opens and validates every target before any ACL is changed. Handles stay
    /// open without delete sharing for the complete batch, so an intended
    /// carve-out cannot be swapped to another object between validation and use.
    /// </summary>
    public void ApplyBatch(IEnumerable<HandleBoundAclMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        var requested = mutations.ToArray();
        if (requested.Length == 0) return;
        if (requested.Length > MaximumTreeEntries)
            throw new InvalidDataException("Protected ACL batch is too large.");

        var duplicate = requested
            .GroupBy(item => NormalizeExpectedPath(item.Path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new InvalidDataException("Protected ACL batch contains a duplicate path.");

        using var privileges = _native.EnableRequiredPrivileges();
        var opened = new List<OpenedMutation>(requested.Length);
        try
        {
            foreach (var mutation in requested)
            {
                ValidatePolicy(mutation.Policy);
                if (!mutation.IsDirectory && mutation.Policy.Aces.Any(
                        ace => ace.InheritanceFlags != default))
                    throw new InvalidDataException(
                        "Protected file ACL mutation cannot contain inheritable ACEs.");
                var expected = NormalizeExpectedPath(mutation.Path);
                var handle = _native.OpenNoFollow(expected);
                try
                {
                    var identity = ValidateOpenedObject(handle, expected, mutation.IsDirectory);
                    opened.Add(new(handle, identity, mutation.Policy));
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            // Recheck every retained handle before the first privileged mutation.
            foreach (var entry in opened)
                EnsureIdentityUnchanged(entry.Handle, entry.Identity);
            foreach (var entry in opened)
                ApplyAndVerify(entry.Handle, entry.Identity, entry.Policy);
        }
        finally
        {
            foreach (var entry in opened) entry.Handle.Dispose();
        }
    }

    /// <summary>
    /// Verifies existing protected objects without repairing them. Every target
    /// is opened no-follow and retained without write/delete sharing until its
    /// identity, SYSTEM owner, protected DACL, and exact ACE set have all been
    /// checked. This is the read-side counterpart to <see cref="ApplyBatch" />
    /// for evidence that must be rejected, rather than trusted after ACL repair.
    /// </summary>
    public void VerifyBatch(IEnumerable<HandleBoundAclMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        var requested = mutations.ToArray();
        if (requested.Length == 0) return;
        if (requested.Length > MaximumTreeEntries)
            throw new InvalidDataException("Protected ACL batch is too large.");

        var duplicate = requested
            .GroupBy(item => NormalizeExpectedPath(item.Path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new InvalidDataException("Protected ACL batch contains a duplicate path.");

        using var privileges = _native.EnableRequiredPrivileges();
        var opened = new List<OpenedMutation>(requested.Length);
        try
        {
            foreach (var mutation in requested)
            {
                ValidatePolicy(mutation.Policy);
                if (!mutation.IsDirectory && mutation.Policy.Aces.Any(
                        ace => ace.InheritanceFlags != default))
                    throw new InvalidDataException(
                        "Protected file ACL verification cannot contain inheritable ACEs.");
                var expected = NormalizeExpectedPath(mutation.Path);
                var handle = _native.OpenNoFollow(expected);
                try
                {
                    var identity = ValidateOpenedObject(handle, expected, mutation.IsDirectory);
                    opened.Add(new(handle, identity, mutation.Policy));
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            foreach (var entry in opened)
            {
                EnsureIdentityUnchanged(entry.Handle, entry.Identity);
                var snapshot = _native.ReadSecurity(entry.Handle);
                EnsureIdentityUnchanged(entry.Handle, entry.Identity);
                if (!IsExact(snapshot, entry.Policy))
                    throw new UnauthorizedAccessException(
                        "Protected ACL target has an unexpected owner or access rule.");
            }
        }
        finally
        {
            foreach (var entry in opened) entry.Handle.Dispose();
        }
    }

    private void ProtectDirectory(
        IHandleBoundAclObject handle,
        string expectedPath,
        HandleBoundAclPolicy directoryPolicy,
        HandleBoundAclPolicy filePolicy,
        HandleBoundAclPolicy barrierDirectoryPolicy,
        int maximumEntries,
        int maximumDepth,
        int depth,
        ref int count)
    {
        if (depth > maximumDepth)
            throw new InvalidDataException("Protected directory tree is too deep.");
        var identity = ValidateOpenedObject(handle, expectedPath, expectedDirectory: true);

        // Non-inheriting root-first barrier: strips hostile access without the
        // kernel propagating an ACE onto an as-yet-unvalidated child reparse point.
        ApplyAndVerify(handle, identity, barrierDirectoryPolicy);

        var children = _native.EnumerateChildren(identity.CanonicalPath)
            .Select(NormalizeExpectedPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (children.Distinct(StringComparer.OrdinalIgnoreCase).Count() != children.Length)
            throw new InvalidDataException("Protected directory enumeration was ambiguous.");

        foreach (var childPath in children)
        {
            if (++count > maximumEntries)
                throw new InvalidDataException("Protected directory tree is too large.");
            EnsureImmediateChild(identity.CanonicalPath, childPath);
            using var child = _native.OpenNoFollow(childPath);
            var childIdentity = ValidateOpenedObject(child, childPath, expectedDirectory: null);
            if (childIdentity.IsDirectory)
            {
                ProtectDirectory(
                    child,
                    childPath,
                    directoryPolicy,
                    filePolicy,
                    barrierDirectoryPolicy,
                    maximumEntries,
                    maximumDepth,
                    depth + 1,
                    ref count);
            }
            else
            {
                ApplyAndVerify(
                    child,
                    childIdentity,
                    filePolicy);
            }
        }

        // Children are now non-reparse, identity-pinned, and independently
        // protected. Mark the parent policy inheritable for future children.
        ApplyAndVerify(
            handle,
            identity,
            directoryPolicy);
    }

    private static void ValidateTreeArguments(
        HandleBoundAclPolicy directoryPolicy,
        HandleBoundAclPolicy filePolicy,
        HandleBoundAclPolicy? barrierDirectoryPolicy,
        int maximumEntries,
        int maximumDepth)
    {
        ValidatePolicy(directoryPolicy);
        ValidatePolicy(filePolicy);
        if (filePolicy.Aces.Any(ace => ace.InheritanceFlags != default))
            throw new InvalidDataException("Protected file policy cannot inherit.");
        if (barrierDirectoryPolicy is not null)
        {
            ValidatePolicy(barrierDirectoryPolicy);
            if (barrierDirectoryPolicy.Aces.Any(ace => ace.InheritanceFlags != default) ||
                barrierDirectoryPolicy.Aces.Any(barrier =>
                    !directoryPolicy.Aces.Any(final =>
                        final.Sid == barrier.Sid &&
                        (barrier.Rights & ~final.Rights) == 0)))
                throw new InvalidDataException(
                    "Protected directory barrier must be non-inheriting and no broader than final policy.");
        }
        if (maximumEntries is < 1 or > MaximumTreeEntries)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumDepth is < 1 or > MaximumTreeDepth)
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
    }

    public static HandleBoundAclPolicy WithoutInheritance(HandleBoundAclPolicy policy)
    {
        ValidatePolicy(policy);
        return policy with
        {
            Aces = policy.Aces
                .Select(ace => ace with { InheritanceFlags = default })
                .ToArray(),
        };
    }

    private HandleBoundObjectIdentity ValidateOpenedObject(
        IHandleBoundAclObject handle,
        string expectedPath,
        bool? expectedDirectory)
    {
        var identity = _native.ReadIdentity(handle);
        if (identity.IsReparsePoint)
            throw new InvalidDataException(
                "Protected ACL target is a reparse point; refusing to follow or mutate it.");
        if (expectedDirectory is not null && identity.IsDirectory != expectedDirectory.Value)
            throw new InvalidDataException("Protected ACL target has the wrong object type.");
        if (!string.Equals(
                NormalizeExpectedPath(identity.CanonicalPath),
                NormalizeExpectedPath(expectedPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Protected ACL target resolved outside its requested path.");
        if (!identity.IsDirectory && identity.NumberOfLinks != 1)
            throw new InvalidDataException(
                "Protected ACL target is a multiply-linked file; refusing cross-tree mutation.");
        return identity;
    }

    private void ApplyAndVerify(
        IHandleBoundAclObject handle,
        HandleBoundObjectIdentity identity,
        HandleBoundAclPolicy policy)
    {
        ValidatePolicy(policy);
        EnsureIdentityUnchanged(handle, identity);
        _native.SetExactSecurity(handle, policy);
        EnsureIdentityUnchanged(handle, identity);
        if (!IsExact(_native.ReadSecurity(handle), policy))
            throw new UnauthorizedAccessException(
                "Protected ACL target retained an unexpected owner or access rule.");
    }

    private void EnsureIdentityUnchanged(
        IHandleBoundAclObject handle,
        HandleBoundObjectIdentity expected)
    {
        var current = _native.ReadIdentity(handle);
        if (!expected.IsSameObject(current))
            throw new InvalidDataException(
                "Protected ACL target identity changed before the privileged mutation.");
    }

    internal static bool IsExact(
        HandleBoundAclSnapshot snapshot,
        HandleBoundAclPolicy policy)
    {
        if (!snapshot.IsDaclProtected ||
            !string.Equals(snapshot.OwnerSid, policy.OwnerSid, StringComparison.Ordinal) ||
            snapshot.Aces.Count != policy.Aces.Count)
            return false;

        var actual = snapshot.Aces
            .GroupBy(ace => ace.Sid, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        return policy.Aces.All(expected =>
            actual.TryGetValue(expected.Sid, out var candidates) &&
            candidates.Length == 1 &&
            candidates[0].Rights == expected.Rights &&
            candidates[0].InheritanceFlags == expected.InheritanceFlags);
    }

    private static void ValidatePolicy(HandleBoundAclPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!string.Equals(policy.OwnerSid, SystemSid, StringComparison.Ordinal))
            throw new InvalidDataException("Protected ACL owner must be LocalSystem.");
        if (policy.Aces.Count is < 2 or > 8 ||
            policy.Aces.Any(ace => string.IsNullOrWhiteSpace(ace.Sid)) ||
            policy.Aces.Select(ace => ace.Sid).Distinct(StringComparer.Ordinal).Count() !=
            policy.Aces.Count)
            throw new InvalidDataException("Protected ACL policy is not exact.");
        if (!policy.Aces.Any(ace =>
                ace.Sid == SystemSid && ace.Rights == FileSystemRights.FullControl) ||
            !policy.Aces.Any(ace =>
                ace.Sid == AdministratorsSid &&
                ace.Rights == FileSystemRights.FullControl))
            throw new InvalidDataException(
                "Protected ACL policy must preserve SYSTEM and Administrators authority.");
    }

    private static void EnsureImmediateChild(string parent, string child)
    {
        var actualParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(child));
        if (actualParent is null || !string.Equals(
                Path.TrimEndingDirectorySeparator(actualParent),
                Path.TrimEndingDirectorySeparator(parent),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Protected tree enumeration escaped its parent.");
    }

    internal static string NormalizeExpectedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        if (!Path.IsPathFullyQualified(full) ||
            full.StartsWith(@"\\", StringComparison.Ordinal) ||
            full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Protected ACL paths must be local and fully qualified.");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private sealed record OpenedMutation(
        IHandleBoundAclObject Handle,
        HandleBoundObjectIdentity Identity,
        HandleBoundAclPolicy Policy);
}

/// <summary>Windows implementation of the no-follow, handle-bound ACL boundary.</summary>
internal sealed class Win32HandleBoundAclNative : IHandleBoundAclNative
{
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    // Read-only sharing lets the handle-bound directory enumerator coexist,
    // while any retained data-write or delete/rename handle makes CreateFileW
    // fail closed. FILE_SHARE_WRITE and FILE_SHARE_DELETE are both absent.
    internal const uint OpenShareMode = FileShareRead;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint OpenFlags = FileFlagBackupSemantics | FileFlagOpenReparsePoint;

    private const uint FileReadAttributes = 0x00000080;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const ushort SeDaclProtected = 0x1000;
    private const int SeFileObject = 1;

    [SupportedOSPlatform("windows")]
    public IDisposable EnableRequiredPrivileges()
    {
        EnsureWindows();
        var backup = WindowsPrivilegeScope.Enable("SeBackupPrivilege");
        try
        {
            var restore = WindowsPrivilegeScope.Enable("SeRestorePrivilege");
            return new CompositeDisposable(restore, backup);
        }
        catch
        {
            backup.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    public IHandleBoundAclObject OpenNoFollow(string path)
    {
        EnsureWindows();
        var handle = CreateFileW(
            path,
            FileReadAttributes | ReadControl | WriteDac | WriteOwner,
            OpenShareMode,
            IntPtr.Zero,
            OpenExisting,
            OpenFlags,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                "Windows could not open the protected ACL target without following links.",
                new Win32Exception(error));
        }
        return new Win32HandleBoundAclObject(handle);
    }

    [SupportedOSPlatform("windows")]
    public HandleBoundObjectIdentity ReadIdentity(IHandleBoundAclObject handle)
    {
        var native = RequireHandle(handle);
        if (!GetFileInformationByHandle(native.Handle, out var information))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var canonical = GetCanonicalPath(native.Handle);
        return new(
            canonical,
            (information.FileAttributes & FileAttributeDirectory) != 0,
            (information.FileAttributes & FileAttributeReparsePoint) != 0,
            information.VolumeSerialNumber,
            ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow,
            information.NumberOfLinks);
    }

    public IReadOnlyList<string> EnumerateChildren(string canonicalDirectoryPath) =>
        Directory.EnumerateFileSystemEntries(
                canonicalDirectoryPath,
                "*",
                SearchOption.TopDirectoryOnly)
            .ToArray();

    [SupportedOSPlatform("windows")]
    public void SetExactSecurity(
        IHandleBoundAclObject handle,
        HandleBoundAclPolicy policy)
    {
        var native = RequireHandle(handle);
        var owner = new SecurityIdentifier(policy.OwnerSid);
        var ownerBytes = new byte[owner.BinaryLength];
        owner.GetBinaryForm(ownerBytes, 0);
        var acl = BuildRawAcl(policy);
        var aclBytes = new byte[acl.BinaryLength];
        acl.GetBinaryForm(aclBytes, 0);

        var ownerPointer = Marshal.AllocHGlobal(ownerBytes.Length);
        var aclPointer = Marshal.AllocHGlobal(aclBytes.Length);
        try
        {
            Marshal.Copy(ownerBytes, 0, ownerPointer, ownerBytes.Length);
            Marshal.Copy(aclBytes, 0, aclPointer, aclBytes.Length);
            var error = SetSecurityInfo(
                native.Handle.DangerousGetHandle(),
                SeFileObject,
                OwnerSecurityInformation |
                DaclSecurityInformation |
                ProtectedDaclSecurityInformation,
                ownerPointer,
                IntPtr.Zero,
                aclPointer,
                IntPtr.Zero);
            if (error != 0)
                throw new Win32Exception((int)error);
        }
        finally
        {
            Marshal.FreeHGlobal(aclPointer);
            Marshal.FreeHGlobal(ownerPointer);
            Array.Clear(aclBytes);
            Array.Clear(ownerBytes);
        }
    }

    [SupportedOSPlatform("windows")]
    public HandleBoundAclSnapshot ReadSecurity(IHandleBoundAclObject handle)
    {
        var native = RequireHandle(handle);
        var error = GetSecurityInfo(
            native.Handle.DangerousGetHandle(),
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out var ownerPointer,
            out _,
            out var daclPointer,
            out _,
            out var descriptorPointer);
        if (error != 0)
            throw new Win32Exception((int)error);
        try
        {
            if (ownerPointer == IntPtr.Zero || daclPointer == IntPtr.Zero)
                throw new InvalidDataException("Protected ACL target returned a null owner or DACL.");
            if (!GetSecurityDescriptorControl(
                    descriptorPointer,
                    out var control,
                    out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var owner = new SecurityIdentifier(ownerPointer).Value;
            var aclSize = unchecked((ushort)Marshal.ReadInt16(daclPointer, 2));
            if (aclSize < 8 || aclSize > 65_535)
                throw new InvalidDataException("Protected ACL target returned an invalid DACL.");
            var aclBytes = new byte[aclSize];
            Marshal.Copy(daclPointer, aclBytes, 0, aclBytes.Length);
            var rawAcl = new RawAcl(aclBytes, 0);
            var aces = new List<HandleBoundAclAce>(rawAcl.Count);
            foreach (GenericAce genericAce in rawAcl)
            {
                if (genericAce is not CommonAce ace ||
                    ace.IsCallback ||
                    ace.AceQualifier != AceQualifier.AccessAllowed ||
                    (ace.AceFlags & AceFlags.Inherited) != 0)
                    throw new InvalidDataException(
                        "Protected ACL target returned a non-exact access rule.");
                var inheritance = InheritanceFlags.None;
                if ((ace.AceFlags & AceFlags.ContainerInherit) != 0)
                    inheritance |= InheritanceFlags.ContainerInherit;
                if ((ace.AceFlags & AceFlags.ObjectInherit) != 0)
                    inheritance |= InheritanceFlags.ObjectInherit;
                if ((ace.AceFlags & ~(AceFlags.ContainerInherit | AceFlags.ObjectInherit)) != 0)
                    throw new InvalidDataException(
                        "Protected ACL target returned unexpected ACE flags.");
                aces.Add(new(
                    ace.SecurityIdentifier.Value,
                    (FileSystemRights)ace.AccessMask,
                    inheritance));
            }
            return new(owner, (control & SeDaclProtected) != 0, aces);
        }
        finally
        {
            if (descriptorPointer != IntPtr.Zero) _ = LocalFree(descriptorPointer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static RawAcl BuildRawAcl(HandleBoundAclPolicy policy)
    {
        var acl = new RawAcl(GenericAcl.AclRevision, policy.Aces.Count);
        foreach (var entry in policy.Aces)
        {
            var flags = AceFlags.None;
            if ((entry.InheritanceFlags & InheritanceFlags.ContainerInherit) != 0)
                flags |= AceFlags.ContainerInherit;
            if ((entry.InheritanceFlags & InheritanceFlags.ObjectInherit) != 0)
                flags |= AceFlags.ObjectInherit;
            acl.InsertAce(acl.Count, new CommonAce(
                flags,
                AceQualifier.AccessAllowed,
                (int)entry.Rights,
                new SecurityIdentifier(entry.Sid),
                isCallback: false,
                opaque: null));
        }
        return acl;
    }

    [SupportedOSPlatform("windows")]
    private static string GetCanonicalPath(SafeFileHandle handle)
    {
        var capacity = 512;
        while (capacity <= 32_768)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)capacity, 0);
            if (length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (length < capacity)
            {
                var value = buffer.ToString();
                if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Protected ACL target is not a local DOS path.");
                if (value.StartsWith(@"\\?\", StringComparison.Ordinal)) value = value[4..];
                return HandleBoundAcl.NormalizeExpectedPath(value);
            }
            capacity = checked((int)length + 1);
        }
        throw new PathTooLongException("Protected ACL target canonical path is too long.");
    }

    private static Win32HandleBoundAclObject RequireHandle(IHandleBoundAclObject handle) =>
        handle as Win32HandleBoundAclObject ??
        throw new ArgumentException("Protected ACL handle is invalid.", nameof(handle));

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Handle-bound ACLs are Windows-only.");
    }

    private sealed class Win32HandleBoundAclObject(SafeFileHandle handle) : IHandleBoundAclObject
    {
        internal SafeFileHandle Handle { get; } = handle;
        public void Dispose() => Handle.Dispose();
    }

    private sealed class CompositeDisposable(IDisposable first, IDisposable second) : IDisposable
    {
        private IDisposable? _first = first;
        private IDisposable? _second = second;

        public void Dispose()
        {
            Interlocked.Exchange(ref _first, null)?.Dispose();
            Interlocked.Exchange(ref _second, null)?.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder path,
        uint pathLength,
        uint flags);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(
        IntPtr handle,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(
        IntPtr handle,
        int objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(
        IntPtr securityDescriptor,
        out ushort control,
        out uint revision);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
