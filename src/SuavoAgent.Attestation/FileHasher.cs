using System.Security.Cryptography;

namespace SuavoAgent.Attestation;

public sealed class FileHasher : IFileHasher
{
    public string? Sha256(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
