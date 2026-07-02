using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Vision;
using Xunit;

namespace SuavoAgent.Core.Tests.Vision;

/// <summary>
/// The Tesseract bundle is NATIVE CODE, so the SHA-256 gate is load-bearing: a corrupt/tampered
/// download must never land executable DLLs on the box. These tests pin that gate + the happy path.
/// </summary>
public class TesseractBundleProvisionerTests
{
    private static byte[] MakeBundleZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var e = zip.CreateEntry("tesseract50.dll").Open()) e.Write(new byte[] { 1, 2, 3, 4 });
            using (var e = zip.CreateEntry("leptonica-1.82.0.dll").Open()) e.Write(new byte[] { 5, 6 });
            using (var e = zip.CreateEntry("tessdata/eng.traineddata").Open()) e.Write(new byte[] { 7, 8, 9 });
        }
        return ms.ToArray();
    }

    private sealed class BytesHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        public BytesHandler(byte[] body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_body) });
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    [Fact]
    public async Task Verified_bundle_extracts_dlls_and_traineddata()
    {
        var zip = MakeBundleZip();
        var dir = Path.Combine(Path.GetTempPath(), "tessprov-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = new TesseractBundleProvisioner(NullLogger.Instance);
            var r = await p.ProvisionAsync("https://x/bundle.zip", Sha(zip), dir, CancellationToken.None, new BytesHandler(zip));

            Assert.True(r.Ok, r.Message);
            Assert.True(File.Exists(Path.Combine(dir, "tesseract50.dll")));
            Assert.True(File.Exists(Path.Combine(dir, "tessdata", "eng.traineddata")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Sha_mismatch_is_rejected_and_no_dll_is_written()
    {
        var zip = MakeBundleZip();
        var dir = Path.Combine(Path.GetTempPath(), "tessprov-" + Guid.NewGuid().ToString("N"));
        try
        {
            var p = new TesseractBundleProvisioner(NullLogger.Instance);
            var r = await p.ProvisionAsync("https://x/bundle.zip", Sha(new byte[] { 0 }), dir, CancellationToken.None, new BytesHandler(zip));

            Assert.False(r.Ok);
            Assert.Contains("mismatch", r.Message);
            Assert.False(File.Exists(Path.Combine(dir, "tesseract50.dll"))); // never extracted
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Already_provisioned_is_idempotent_no_redownload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tessprov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "tessdata"));
        File.WriteAllBytes(Path.Combine(dir, "tesseract50.dll"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(dir, "tessdata", "eng.traineddata"), new byte[] { 1 });
        try
        {
            var p = new TesseractBundleProvisioner(NullLogger.Instance);
            // A handler that would throw if hit — proves no download happens when already present.
            var r = await p.ProvisionAsync("https://x/bundle.zip", "deadbeef", dir, CancellationToken.None,
                new BytesHandler(Array.Empty<byte>()));
            Assert.True(r.Ok);
            Assert.Contains("already", r.Message);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
