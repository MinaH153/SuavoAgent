using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Core.Config;

/// <summary>
/// DPAPI LocalMachine credential store at %ProgramData%\SuavoAgent\credentials.dat.
/// The file contains only opaque DPAPI blobs. Malformed/tampered stores fail closed:
/// a write never silently replaces unreadable credential material.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : IEncryptedCredentialStore
{
    private const int MaxStoreBytes = 1024 * 1024;
    private static readonly byte[] Entropy = "SuavoAgent.CredentialStore.v1"u8.ToArray();
    private static readonly ConcurrentDictionary<string, object> PathGates =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _filePath;
    private readonly object _gate;

    public DpapiCredentialStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "credentials.dat"))
    {
    }

    public DpapiCredentialStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
        _gate = PathGates.GetOrAdd(_filePath, static _ => new object());
    }

    public string? Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var map = Read();
            if (!map.TryGetValue(key, out var blob))
                return null;

            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(blob);
            }
            catch (FormatException ex)
            {
                throw new InvalidDataException("Credential store contains an invalid protected value.", ex);
            }

            try
            {
                var clearBytes = ProtectedData.Unprotect(
                    protectedBytes,
                    Entropy,
                    DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(clearBytes);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidDataException("Credential store authentication failed.", ex);
            }
        }
    }

    public void Set(string key, string value) => SetMany(
        new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value });

    public void SetMany(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0) return;

        lock (_gate)
        {
            // Read first. Corruption/tampering aborts without replacing the file.
            var map = Read();
            foreach (var pair in values)
            {
                ValidateEntry(pair.Key, pair.Value);
                var encrypted = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(pair.Value),
                    Entropy,
                    DataProtectionScope.LocalMachine);
                map[pair.Key] = Convert.ToBase64String(encrypted);
            }
            Write(map);
        }
    }

    public void Delete(string key) => DeleteMany([key]);

    public void DeleteMany(IReadOnlyCollection<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0) return;
        foreach (var key in keys) ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            var map = Read();
            var changed = false;
            foreach (var key in keys) changed |= map.Remove(key);
            if (changed)
                Write(map);
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        if ((File.GetAttributes(_filePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Credential store cannot be a reparse point.");

        try
        {
            using var stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaxStoreBytes)
                throw new InvalidDataException("Credential store size is invalid.");
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                      ?? throw new InvalidDataException("Credential store root is invalid.");
            if (stream.Position != stream.Length || map.Count > 64)
                throw new InvalidDataException("Credential store shape is invalid.");
            foreach (var pair in map)
            {
                ValidateProtectedEntry(pair.Key, pair.Value);
                try { _ = Convert.FromBase64String(pair.Value); }
                catch (FormatException ex)
                {
                    throw new InvalidDataException("Credential store contains an invalid protected value.", ex);
                }
            }
            return new Dictionary<string, string>(map, StringComparer.Ordinal);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Credential store JSON is invalid.", ex);
        }
    }

    private void Write(Dictionary<string, string> map)
    {
        var directory = Path.GetDirectoryName(_filePath)
                        ?? throw new InvalidOperationException("Credential store directory is unavailable.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $".{Path.GetFileName(_filePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, map);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static void ValidateEntry(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Length > 128 || value.Length > 16 * 1024 || key.Any(char.IsControl))
            throw new InvalidDataException("Credential store entry exceeds its safe bounds.");
    }

    private static void ValidateProtectedEntry(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Length > 128 || value.Length > 64 * 1024 || key.Any(char.IsControl))
            throw new InvalidDataException("Credential store protected entry exceeds its safe bounds.");
    }
}
