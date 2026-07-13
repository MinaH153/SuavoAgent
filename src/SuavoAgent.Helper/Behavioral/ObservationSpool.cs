using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.Behavioral;

public interface IObservationSpoolProtection
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

public interface IObservationSpoolAccessControl
{
    void PrepareAndValidateDirectory(string directory);
    void ProtectAndValidateFile(string path);
    void ValidateDirectory(string directory);
    void ValidateFile(string path);
}

/// <summary>
/// Atomic encrypted observation spool. Only DPAPI ciphertext ever reaches
/// disk; the lock prevents two Helper processes from splitting one stream.
/// </summary>
public sealed class ObservationSpool : IBehavioralEventSpool
{
    private const int MaximumCiphertextBytes = 16 * 1024 * 1024;
    private const int MaximumPlaintextBytes = 12 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 64,
    };

    private readonly string _path;
    private readonly string _directory;
    private readonly IObservationSpoolProtection _protection;
    private readonly IObservationSpoolAccessControl _accessControl;
    private readonly FileStream _processLock;
    private readonly bool _expectExistingSpool;
    private bool _disposed;

    public ObservationSpool(
        string path,
        IObservationSpoolProtection protection,
        IObservationSpoolAccessControl accessControl)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An observation spool path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path)
            ?? throw new BehavioralEventPersistenceException("observation_spool_path_invalid");
        _protection = protection ?? throw new ArgumentNullException(nameof(protection));
        _accessControl = accessControl ?? throw new ArgumentNullException(nameof(accessControl));

        FileStream? processLock = null;
        try
        {
            _accessControl.PrepareAndValidateDirectory(_directory);
            var lockPath = _path + ".lock";
            _expectExistingSpool = File.Exists(lockPath);
            processLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough);
            _accessControl.ProtectAndValidateFile(lockPath);
            CleanStaleTemporaryFiles();
            _processLock = processLock;
            processLock = null;
        }
        catch (BehavioralEventPersistenceException)
        {
            processLock?.Dispose();
            throw;
        }
        catch (IOException ex)
        {
            processLock?.Dispose();
            throw new BehavioralEventPersistenceException("observation_spool_locked", ex);
        }
        catch (Exception ex)
        {
            processLock?.Dispose();
            throw new BehavioralEventPersistenceException("observation_spool_acl_invalid", ex);
        }
    }

    public static ObservationSpool CreateProduction(string channel)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Observation spool requires Windows DPAPI and ACLs.");
        if (!BehavioralEventChannels.IsKnown(channel))
            throw new ArgumentException("Unknown observation channel.", nameof(channel));

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SuavoAgent",
            "observations");
        return new ObservationSpool(
            Path.Combine(root, channel + ".spool"),
            new DpapiObservationSpoolProtection(),
            new WindowsObservationSpoolAccessControl());
    }

    public BehavioralEventBufferState? Load()
    {
        ThrowIfDisposed();
        try
        {
            _accessControl.ValidateDirectory(_directory);
            if (!File.Exists(_path))
            {
                if (_expectExistingSpool)
                    throw new BehavioralEventPersistenceException("observation_spool_missing");
                return null;
            }
            _accessControl.ValidateFile(_path);

            var length = new FileInfo(_path).Length;
            if (length <= 0 || length > MaximumCiphertextBytes)
                throw new BehavioralEventPersistenceException("observation_spool_size_invalid");

            var ciphertext = File.ReadAllBytes(_path);
            byte[]? plaintext = null;
            try
            {
                plaintext = _protection.Unprotect(ciphertext);
                if (plaintext.Length <= 0 || plaintext.Length > MaximumPlaintextBytes)
                    throw new BehavioralEventPersistenceException("observation_spool_size_invalid");
                return JsonSerializer.Deserialize<BehavioralEventBufferState>(plaintext, JsonOptions)
                    ?? throw new BehavioralEventPersistenceException("observation_spool_corrupt");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (BehavioralEventPersistenceException)
        {
            throw;
        }
        catch (CryptographicException ex)
        {
            throw new BehavioralEventPersistenceException("observation_spool_unprotect_failed", ex);
        }
        catch (JsonException ex)
        {
            throw new BehavioralEventPersistenceException("observation_spool_corrupt", ex);
        }
        catch (Exception ex)
        {
            throw new BehavioralEventPersistenceException("observation_spool_read_failed", ex);
        }
    }

    public void Save(BehavioralEventBufferState state)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(state);
        byte[]? plaintext = null;
        byte[]? ciphertext = null;
        var temporary = Path.Combine(_directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            _accessControl.ValidateDirectory(_directory);
            if (File.Exists(_path)) _accessControl.ValidateFile(_path);
            plaintext = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
            if (plaintext.Length > MaximumPlaintextBytes)
                throw new BehavioralEventPersistenceException("observation_spool_size_invalid");
            ciphertext = _protection.Protect(plaintext);
            if (ciphertext.Length <= 0 || ciphertext.Length > MaximumCiphertextBytes)
                throw new BehavioralEventPersistenceException("observation_spool_size_invalid");

            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(ciphertext);
                stream.Flush(flushToDisk: true);
            }
            _accessControl.ProtectAndValidateFile(temporary);
            File.Move(temporary, _path, overwrite: true);
            _accessControl.ValidateFile(_path);
        }
        catch (BehavioralEventPersistenceException)
        {
            throw;
        }
        catch (CryptographicException ex)
        {
            throw new BehavioralEventPersistenceException("observation_spool_protect_failed", ex);
        }
        catch (Exception ex)
        {
            throw new BehavioralEventPersistenceException("observation_spool_write_failed", ex);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
            if (ciphertext is not null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processLock.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ObservationSpool));
    }

    private void CleanStaleTemporaryFiles()
    {
        var pattern = $".{Path.GetFileName(_path)}.*.tmp";
        foreach (var stale in Directory.EnumerateFiles(_directory, pattern, SearchOption.TopDirectoryOnly))
        {
            _accessControl.ValidateFile(stale);
            File.Delete(stale);
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class DpapiObservationSpoolProtection : IObservationSpoolProtection
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(
        "SuavoAgent/ObservationSpool/v1");

    public byte[] Protect(ReadOnlySpan<byte> plaintext) =>
        ProtectedData.Protect(plaintext.ToArray(), Entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(ReadOnlySpan<byte> ciphertext) =>
        ProtectedData.Unprotect(ciphertext.ToArray(), Entropy, DataProtectionScope.CurrentUser);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsObservationSpoolAccessControl : IObservationSpoolAccessControl
{
    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public void PrepareAndValidateDirectory(string directory)
    {
        ValidateLocalNonReparsePath(directory);
        Directory.CreateDirectory(directory);

        var currentUser = CurrentUserSid();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(DirectoryRule(SystemSid, FileSystemRights.FullControl));
        security.AddAccessRule(DirectoryRule(AdministratorsSid, FileSystemRights.FullControl));
        security.AddAccessRule(DirectoryRule(
            currentUser,
            FileSystemRights.ReadAndExecute |
            FileSystemRights.Write |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles));
        new DirectoryInfo(directory).SetAccessControl(security);
        ValidateDirectory(directory);
    }

    public void ProtectAndValidateFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(FileRule(SystemSid, FileSystemRights.FullControl));
        security.AddAccessRule(FileRule(AdministratorsSid, FileSystemRights.FullControl));
        security.AddAccessRule(FileRule(
            CurrentUserSid(),
            FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete));
        new FileInfo(path).SetAccessControl(security);
        ValidateFile(path);
    }

    public void ValidateDirectory(string directory)
    {
        ValidateLocalNonReparsePath(directory);
        if (!Directory.Exists(directory))
            throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
        ValidateRules(
            new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access),
            FileSystemRights.ReadAndExecute |
            FileSystemRights.Write |
            FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles);
    }

    public void ValidateFile(string path)
    {
        if (!File.Exists(path))
            throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
        if (new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new BehavioralEventPersistenceException("observation_spool_reparse_rejected");
        ValidateRules(
            new FileInfo(path).GetAccessControl(AccessControlSections.Access),
            FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Delete);
    }

    private static void ValidateRules(FileSystemSecurity security, FileSystemRights requiredUserRights)
    {
        if (!security.AreAccessRulesProtected)
            throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");

        var currentUser = CurrentUserSid();
        var systemRights = (FileSystemRights)0;
        var administratorRights = (FileSystemRights)0;
        var userRights = (FileSystemRights)0;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>();
        foreach (var rule in rules)
        {
            if (rule.IsInherited || rule.AccessControlType != AccessControlType.Allow)
                throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (!sid.Equals(SystemSid) && !sid.Equals(AdministratorsSid) && !sid.Equals(currentUser))
                throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
            if ((rule.FileSystemRights & (FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)) != 0
                && sid.Equals(currentUser))
            {
                throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
            }
            if (sid.Equals(SystemSid)) systemRights |= rule.FileSystemRights;
            if (sid.Equals(AdministratorsSid)) administratorRights |= rule.FileSystemRights;
            if (sid.Equals(currentUser)) userRights |= rule.FileSystemRights;
        }
        if ((systemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl
            || (administratorRights & FileSystemRights.FullControl) != FileSystemRights.FullControl
            || (userRights & requiredUserRights) != requiredUserRights)
        {
            throw new BehavioralEventPersistenceException("observation_spool_acl_invalid");
        }
    }

    private static void ValidateLocalNonReparsePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw new BehavioralEventPersistenceException("observation_spool_unc_rejected");
        for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new BehavioralEventPersistenceException("observation_spool_reparse_rejected");
        }
    }

    private static SecurityIdentifier CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User
        ?? throw new BehavioralEventPersistenceException("observation_spool_user_sid_unavailable");

    private static FileSystemAccessRule DirectoryRule(
        IdentityReference identity,
        FileSystemRights rights) =>
        new(
            identity,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);

    private static FileSystemAccessRule FileRule(
        IdentityReference identity,
        FileSystemRights rights) =>
        new(identity, rights, AccessControlType.Allow);
}
