using System;
using System.IO;
using System.Security.Cryptography;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Guards checksum-signature verification against the REAL signed-artifact shape.
/// The release signs checksums.sha256 with `openssl dgst -sha256 -sign`, producing
/// a BINARY, DER-encoded ECDSA P-256 signature. Earlier code read the .sig as a hex
/// string and verified with the IEEE-P1363 format — which threw "input is not a
/// valid hex string" and broke every install at the binary-download phase. These
/// fixtures are the public v3.15.2 release artifacts.
/// </summary>
public sealed class BinaryDownloaderTests
{
    [Theory]
    [InlineData("v3.20.0")]
    [InlineData("3.20.0")]
    [InlineData("v3.20.0-rc.1")]
    public void ReleaseTag_accepts_only_pinned_version_identifiers(string releaseTag)
    {
        Assert.True(BinaryDownloader.IsValidReleaseTag(releaseTag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("../main")]
    [InlineData("v3.20")]
    [InlineData("v3.20.0/other")]
    public void ReleaseTag_rejects_unpinned_or_path_like_values(string releaseTag)
    {
        Assert.False(BinaryDownloader.IsValidReleaseTag(releaseTag));
    }

    private const string ChecksumsB64 =
        "MjZlYWVmZDFlMDA0MDE0OTU2YWQxYTA3NjAyNGUwMGE4MTRmMmQ2N2NjMzRhY2RkMmExNzA3YmNlYWRjNzBiYSAgU3Vhdm9BZ2VudC5Db3JlLmV4ZQoxOWI2NDE0ZWE4YWNlMDFlY2I2OWZjZTY2Mjg5NjAxNTRhNzk3OWRlNTIwZGI5NDViM2ZmNWYzNThlZDE3MDU4ICBTdWF2b0FnZW50LkJyb2tlci5leGUKNDc4NTQzNTExZjBkOTI1YjJkYWZlNGEyYzEyOTEzYTdiNmJjYzNiNTAwNDVjYjk3MzE0OTUwZGQ2NDM2YjU1NyAgU3Vhdm9BZ2VudC5IZWxwZXIuZXhlCjE4NjlmZGRlMWVjMjhhZTdjN2RlODExOWQwZmFmMzdiY2NjNWQyODg1ZWIwMzgzOTIzOWNiMGQ1YjM0ZTEwNTUgIFN1YXZvQWdlbnQuV2F0Y2hkb2cuZXhlCmFlMTEyYzQyMzY0NjBhMzg5MDg4YWRkYTQ4M2U3MzZiNWMxZjUyM2Y3MWJjMzY5MzU1ZmE4NDBkYmMyNmNmM2EgIFN1YXZvU2V0dXAuZXhlCmY3NmFiYWZmYzA3MzM1NmRmM2Y1YWQ0NTFmYTE3MTc2MjIxNTc1ZTg1MGZjNDZmZDJmNGFlMGY4NWNkNjcxMWUgIHN1YXZvYWdlbnQtdjMuMTUuMi13aW4teDY0LnppcApmOTkyMGM3MmU1ODQzY2Y1YTAyYzI4NTg5MTcxN2ViYzlmYzZhMzAyZTBjYmNhMjMzNTk5Y2RiZTU1NjYxNGQ3ICBmaWVsZC1yZWxlYXNlLXJlY2VpcHQuanNvbgo=";

    private const string SignatureB64 =
        "MEUCIHC8OionrBSFw7uMKbOxnaOUYUrJQ3+59HnEBKqHpwfzAiEA53k2glAv/TjtdDDlAucY1hcoyfIdSQUDupo0bbOpGNE=";

    [Fact]
    public void Verifies_real_release_der_signature()
    {
        var checksums = Convert.FromBase64String(ChecksumsB64);
        var signature = Convert.FromBase64String(SignatureB64);

        Assert.True(BinaryDownloader.VerifyChecksumSignature(checksums, signature));
    }

    [Fact]
    public void Rejects_tampered_checksums()
    {
        var checksums = Convert.FromBase64String(ChecksumsB64);
        checksums[0] ^= 0xFF; // flip a byte of the signed payload
        var signature = Convert.FromBase64String(SignatureB64);

        Assert.False(BinaryDownloader.VerifyChecksumSignature(checksums, signature));
    }

    [Fact]
    public void Rotation_registry_accepts_checksum_signed_by_v2()
    {
        using var v1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var v2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var checksums = System.Text.Encoding.UTF8.GetBytes("abc  SuavoAgent.Core.exe\n");
        var signature = v2.SignData(
            checksums,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SuavoAgent.Contracts.Maintenance.OtaUpdateTrust.LegacyV1KeyId] =
                Convert.ToBase64String(v1.ExportSubjectPublicKeyInfo()),
            [SuavoAgent.Contracts.Maintenance.OtaUpdateTrust.CurrentV2KeyId] =
                Convert.ToBase64String(v2.ExportSubjectPublicKeyInfo()),
        };

        Assert.True(BinaryDownloader.VerifyChecksumSignature(checksums, signature, roots));
        Assert.True(BinaryDownloader.VerifyChecksumSignatureForKeyId(
            SuavoAgent.Contracts.Maintenance.OtaUpdateTrust.CurrentV2KeyId,
            checksums,
            signature,
            roots));
        Assert.False(BinaryDownloader.VerifyChecksumSignatureForKeyId(
            SuavoAgent.Contracts.Maintenance.OtaUpdateTrust.LegacyV1KeyId,
            checksums,
            signature,
            roots));
    }

    [Fact]
    public async Task Metadata_header_over_cap_is_rejected_before_body_read()
    {
        var content = new TrackingContent([1]);
        content.Headers.ContentLength = BinaryDownloader.MaxChecksumManifestBytes + 1;
        using var http = new HttpClient(new StaticResponseHandler(content));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDownloader.DownloadBoundedBytesAsync(
                http,
                "https://assets.example/checksums.sha256",
                BinaryDownloader.MaxChecksumManifestBytes,
                CancellationToken.None));
        Assert.False(content.StreamOpened);
    }

    [Fact]
    public async Task Metadata_without_length_is_still_stopped_by_streaming_cap()
    {
        var bytes = new byte[BinaryDownloader.MaxChecksumSignatureBytes + 1];
        using var http = new HttpClient(new StaticResponseHandler(
            new UnknownLengthContent(bytes)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDownloader.DownloadBoundedBytesAsync(
                http,
                "https://assets.example/checksums.sha256.sig",
                BinaryDownloader.MaxChecksumSignatureBytes,
                CancellationToken.None));
    }

    [Fact]
    public async Task Metadata_at_exact_cap_is_returned_without_unbounded_buffering()
    {
        var bytes = Enumerable.Range(0, BinaryDownloader.MaxChecksumSignatureBytes)
            .Select(value => (byte)value)
            .ToArray();
        using var http = new HttpClient(new StaticResponseHandler(
            new UnknownLengthContent(bytes)));

        var downloaded = await BinaryDownloader.DownloadBoundedBytesAsync(
            http,
            "https://assets.example/checksums.sha256.sig",
            BinaryDownloader.MaxChecksumSignatureBytes,
            CancellationToken.None);

        Assert.Equal(bytes, downloaded);
    }

    // Regression for the 2026-06-05 brick: the GUI installer placed binaries but never wrote
    // binaries.manifest, so the Broker's integrity guard rejected the Helper -> agent blind. The
    // manifest MUST carry the Helper's on-disk sha256 in the exact shape the Broker reads
    // (manifest["SuavoAgent.Helper.exe"] == lowercase-hex sha256), and must bind all five executables.
    [Fact]
    public void WriteBinariesManifest_carries_helper_hash_the_broker_compares()
    {
        var dir = Path.Combine(Path.GetTempPath(), "suavo-manifest-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, "binaries.manifest");
        try
        {
            foreach (var binary in BinaryDownloader.InstalledCohort)
                File.WriteAllText(Path.Combine(dir, binary), binary + "-bytes");
            var helperPath = Path.Combine(dir, "SuavoAgent.Helper.exe");

            Assert.True(BinaryDownloader.WriteBinariesManifest(dir, manifestPath));

            Assert.True(File.Exists(manifestPath));
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));

            Assert.True(doc.RootElement.TryGetProperty("SuavoAgent.Helper.exe", out var helperEl));
            var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(helperPath))).ToLowerInvariant();
            Assert.Equal(expected, helperEl.GetString());

            Assert.Equal(BinaryDownloader.InstalledCohort.Count, doc.RootElement.EnumerateObject().Count());
            Assert.All(
                BinaryDownloader.InstalledCohort,
                binary => Assert.True(doc.RootElement.TryGetProperty(binary, out _), $"Missing {binary}"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void WriteBinariesManifest_rejects_partial_cohort_and_removes_stale_manifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "suavo-manifest-partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var manifestPath = Path.Combine(dir, "binaries.manifest");
        try
        {
            File.WriteAllText(Path.Combine(dir, "SuavoAgent.Core.exe"), "core-bytes");
            File.WriteAllText(manifestPath, "{\"stale\":\"manifest\"}");

            Assert.False(BinaryDownloader.WriteBinariesManifest(dir, manifestPath));
            Assert.False(File.Exists(manifestPath));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // Regression for the 2026-06-10 fresh-install brick: SuavoAgent.Watchdog.exe was
    // published in every release but absent from the download list, so ServiceInstaller
    // found no Watchdog binary and bailed BEFORE registering any service — while the GUI
    // still reported "Installation complete". The download list must carry every
    // executable the service installer requires.
    [Fact]
    public void RequiredBinaries_include_every_service_executable_setup_must_place()
    {
        string[] required =
        [
            "SuavoAgent.Core.exe",
            "SuavoAgent.Broker.exe",
            "SuavoAgent.Helper.exe",
            "SuavoAgent.Watchdog.exe",
        ];
        Assert.Equal(required, BinaryDownloader.RequiredBinaries);
    }

    [Fact]
    public void Installed_cohort_adds_native_maintenance_without_downloading_it_twice()
    {
        Assert.DoesNotContain(
            SuavoAgent.Contracts.Maintenance.MaintenanceContract.ExecutableName,
            BinaryDownloader.RequiredBinaries);
        Assert.Equal(
            [
                .. BinaryDownloader.RequiredBinaries,
                SuavoAgent.Contracts.Maintenance.MaintenanceContract.ExecutableName,
            ],
            BinaryDownloader.InstalledCohort);
    }

    [Fact]
    public void HashMatches_accepts_correct_rejects_tampered_and_is_case_insensitive()
    {
        // QA wave2.5: the per-binary tamper gate DownloadAndVerifyAsync uses on a fresh install.
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "the signed binary contents");
            var realHex = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tmp))).ToLowerInvariant();

            Assert.True(BinaryDownloader.HashMatches(tmp, realHex));                      // correct hash accepted
            Assert.True(BinaryDownloader.HashMatches(tmp, realHex.ToUpperInvariant()));   // hex case-insensitive
            Assert.False(BinaryDownloader.HashMatches(tmp, new string('0', 64)));         // wrong hash rejected

            // A tampered binary (content changed) no longer matches the original signed checksum.
            File.WriteAllText(tmp, "the signed binary contents TAMPERED");
            Assert.False(BinaryDownloader.HashMatches(tmp, realHex));
        }
        finally { File.Delete(tmp); }
    }

    private sealed class StaticResponseHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = content,
        });
    }

    private sealed class TrackingContent(byte[] bytes) : HttpContent
    {
        public bool StreamOpened { get; private set; }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context)
        {
            StreamOpened = true;
            return stream.WriteAsync(bytes).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
