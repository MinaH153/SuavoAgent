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
    internal const long MaxDownloadBytes = 64L * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly string _assetUrl;
    private readonly string _expectedSha256;
    private readonly long _maxDownloadBytes;

    public VcRedistProvider(HttpClient http, string assetUrl, string expectedSha256)
        : this(http, assetUrl, expectedSha256, MaxDownloadBytes)
    {
    }

    internal VcRedistProvider(
        HttpClient http,
        string assetUrl,
        string expectedSha256,
        long maxDownloadBytes)
    {
        _http = http;
        _assetUrl = assetUrl;
        _expectedSha256 = expectedSha256.Trim().ToLowerInvariant();
        if (maxDownloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxDownloadBytes));
        _maxDownloadBytes = maxDownloadBytes;
    }

    public async Task<string> EnsureLocalAsync(string destPath, CancellationToken ct)
    {
        if (File.Exists(destPath))
            throw new VcRedistVerificationException(
                "vc_redist destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        var tempPath = destPath + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await _http.GetAsync(
                _assetUrl,
                HttpCompletionOption.ResponseHeadersRead,
                ct);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long declared &&
                (declared <= 0 || declared > _maxDownloadBytes))
                throw new VcRedistVerificationException(
                    "vc_redist declared an invalid or oversized payload.");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using (var destination = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.WriteThrough))
            {
                var buffer = new byte[128 * 1024];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    if (total > _maxDownloadBytes - read)
                        throw new VcRedistVerificationException(
                            "vc_redist exceeded its streaming size limit.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    total += read;
                }
                if (total == 0)
                    throw new VcRedistVerificationException("vc_redist download was empty.");
                await destination.FlushAsync(ct);
                destination.Flush(flushToDisk: true);
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != _expectedSha256)
                throw new VcRedistVerificationException(
                    $"vc_redist SHA-256 mismatch: expected {_expectedSha256}, got {actual}");
            // The final name must never replace an existing file. Production
            // gives this provider a random name inside a create-new protected
            // directory; a second exact-byte verification follows this move.
            File.Move(tempPath, destPath);
            return destPath;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }
}

public sealed class VcRedistVerificationException : Exception
{
    public VcRedistVerificationException(string message) : base(message) { }
}
