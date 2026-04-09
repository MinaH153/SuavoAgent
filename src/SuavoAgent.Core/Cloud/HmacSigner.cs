using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Core.Cloud;

public sealed class HmacSigner
{
    private readonly byte[] _keyBytes;

    public HmacSigner(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _keyBytes = Encoding.UTF8.GetBytes(apiKey);
    }

    public string Sign(string timestamp, string body)
    {
        var message = $"{timestamp}:{body}";
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var hash = HMACSHA256.HashData(_keyBytes, messageBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsWithinReplayWindow(string timestamp, TimeSpan window)
    {
        if (!DateTimeOffset.TryParse(timestamp, out var parsed))
            return false;
        var age = DateTimeOffset.UtcNow - parsed;
        return age >= TimeSpan.Zero && age <= window;
    }
}
