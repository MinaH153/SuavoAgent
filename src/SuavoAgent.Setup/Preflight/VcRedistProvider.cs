// src/SuavoAgent.Setup/Preflight/VcRedistProvider.cs
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Setup.Preflight;

/// <summary>Downloads vc_redist.x64.exe from the pinned GitHub release asset and SHA-256-verifies it.</summary>
public sealed class VcRedistProvider
{
    private readonly HttpClient _http;
    private readonly string _assetUrl;
    private readonly string _expectedSha256;

    public VcRedistProvider(HttpClient http, string assetUrl, string expectedSha256)
    {
        _http = http;
        _assetUrl = assetUrl;
        _expectedSha256 = expectedSha256.Trim().ToLowerInvariant();
    }

    public async Task<string> EnsureLocalAsync(string destPath, CancellationToken ct)
    {
        var bytes = await _http.GetByteArrayAsync(_assetUrl, ct);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (actual != _expectedSha256)
            throw new VcRedistVerificationException(
                $"vc_redist SHA-256 mismatch: expected {_expectedSha256}, got {actual}");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        await File.WriteAllBytesAsync(destPath, bytes, ct);
        return destPath;
    }
}

public sealed class VcRedistVerificationException : Exception
{
    public VcRedistVerificationException(string message) : base(message) { }
}
