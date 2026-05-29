using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Core.Config;

/// <summary>
/// Windows DPAPI-backed credential store. Each value is encrypted with
/// <see cref="DataProtectionScope.LocalMachine"/> (matching
/// <see cref="CredentialProtector"/>) and persisted to a small JSON file
/// under %ProgramData%\SuavoAgent. The file holds only opaque base64 DPAPI
/// blobs — never plaintext.
///
/// Windows-only. The factory (<see cref="CredentialStoreFactory"/>) selects
/// this on Windows; macOS Keychain is a v5 follow-up.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : IEncryptedCredentialStore
{
    private readonly string _filePath;
    private readonly object _gate = new();

    /// <summary>Production path: %ProgramData%\SuavoAgent\credentials.dat.</summary>
    public DpapiCredentialStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "credentials.dat"))
    {
    }

    /// <summary>Test/override ctor with an explicit file path.</summary>
    public DpapiCredentialStore(string filePath)
    {
        _filePath = filePath;
    }

    public string? Get(string key)
    {
        lock (_gate)
        {
            var map = Read();
            if (!map.TryGetValue(key, out var blob) || string.IsNullOrEmpty(blob))
                return null;
            var dec = ProtectedData.Unprotect(
                Convert.FromBase64String(blob), null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(dec);
        }
    }

    public void Set(string key, string value)
    {
        lock (_gate)
        {
            var map = Read();
            var enc = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine);
            map[key] = Convert.ToBase64String(enc);
            Write(map);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var map = Read();
            if (map.Remove(key))
                Write(map);
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_filePath))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Corrupt store — treat as empty rather than crashing the agent.
            // A fresh Set() will overwrite it.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, string> map)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(map));
    }
}
