using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Attestation;
using Xunit;

namespace SuavoAgent.Attestation.Tests;

public class AttestationVerifierTests
{
    private sealed class FakeFetcher : IManifestFetcher
    {
        public SignedManifestPayload? Payload { get; set; }
        public Task<SignedManifestPayload?> FetchAsync(string version, CancellationToken ct) => Task.FromResult(Payload);
    }

    private sealed class FakeVerifier : IManifestSignatureVerifier
    {
        public bool Result { get; set; } = true;
        public bool Verify(string json, byte[] sig) => Result;
    }

    private sealed class FakeHasher : IFileHasher
    {
        public Dictionary<string, string?> Responses { get; } = new();
        public string? Sha256(string path) =>
            Responses.TryGetValue(Path.GetFileName(path), out var h) ? h : null;
    }

    private static (string installDir, string version, AttestationManifest manifest, SignedManifestPayload payload)
        MakeFixture()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"att-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);

        var manifest = new AttestationManifest
        {
            Version = "3.13.7",
            Framework = "net8.0",
            Runtime = "win-x64",
            ManifestVersion = "1.0.0",
            GeneratedAt = DateTimeOffset.UtcNow,
            Files = new List<AttestedFile>
            {
                new("SuavoAgent.Core.exe", "aaa", 1024),
                new("SuavoAgent.Broker.exe", "bbb", 2048)
            }
        };
        var json = JsonSerializer.Serialize(manifest);
        return (tmp, manifest.Version, manifest, new SignedManifestPayload(json, new byte[] { 0x01 }));
    }

    [Fact]
    public async Task Verify_AllFilesMatch_ReturnsVerified()
    {
        var (installDir, version, _, payload) = MakeFixture();
        try
        {
            var fetcher = new FakeFetcher { Payload = payload };
            var sig = new FakeVerifier { Result = true };
            var hasher = new FakeHasher
            {
                Responses = { ["SuavoAgent.Core.exe"] = "aaa", ["SuavoAgent.Broker.exe"] = "bbb" }
            };
            var halt = new AttestationHaltSignal();

            var v = new AttestationVerifier(fetcher, sig, hasher, halt,
                NullLogger<AttestationVerifier>.Instance, installDir, version);

            var result = await v.VerifyAsync(CancellationToken.None);

            Assert.Equal(AttestationStatus.Verified, result.Status);
            Assert.False(halt.IsHalted);
        }
        finally { Directory.Delete(installDir, true); }
    }

    [Fact]
    public async Task Verify_FileHashMismatch_ReturnsMismatchAndHalts()
    {
        var (installDir, version, _, payload) = MakeFixture();
        try
        {
            var fetcher = new FakeFetcher { Payload = payload };
            var sig = new FakeVerifier { Result = true };
            var hasher = new FakeHasher
            {
                // Core is tampered (hash differs)
                Responses = { ["SuavoAgent.Core.exe"] = "tampered", ["SuavoAgent.Broker.exe"] = "bbb" }
            };
            var halt = new AttestationHaltSignal();

            var v = new AttestationVerifier(fetcher, sig, hasher, halt,
                NullLogger<AttestationVerifier>.Instance, installDir, version);

            var result = await v.VerifyAsync(CancellationToken.None);

            Assert.Equal(AttestationStatus.Mismatch, result.Status);
            Assert.NotNull(result.Mismatches);
            Assert.Single(result.Mismatches!);
            Assert.Equal("SuavoAgent.Core.exe", result.Mismatches![0].FileName);
            Assert.True(halt.IsHalted);
        }
        finally { Directory.Delete(installDir, true); }
    }

    [Fact]
    public async Task Verify_FileMissing_ReturnsMismatch()
    {
        var (installDir, version, _, payload) = MakeFixture();
        try
        {
            var fetcher = new FakeFetcher { Payload = payload };
            var sig = new FakeVerifier { Result = true };
            var hasher = new FakeHasher
            {
                // Broker missing (hash returns null)
                Responses = { ["SuavoAgent.Core.exe"] = "aaa" }
            };
            var halt = new AttestationHaltSignal();

            var v = new AttestationVerifier(fetcher, sig, hasher, halt,
                NullLogger<AttestationVerifier>.Instance, installDir, version);

            var result = await v.VerifyAsync(CancellationToken.None);

            Assert.Equal(AttestationStatus.Mismatch, result.Status);
            Assert.Contains(result.Mismatches!, m => m.FileName == "SuavoAgent.Broker.exe");
            Assert.Contains(result.Mismatches!, m => m.Reason == "file_missing");
        }
        finally { Directory.Delete(installDir, true); }
    }

    [Fact]
    public async Task Verify_NetworkFailure_DoesNotHalt()
    {
        var (installDir, version, _, _) = MakeFixture();
        try
        {
            var fetcher = new FakeFetcher { Payload = null }; // fetch failed
            var sig = new FakeVerifier();
            var hasher = new FakeHasher();
            var halt = new AttestationHaltSignal();

            var v = new AttestationVerifier(fetcher, sig, hasher, halt,
                NullLogger<AttestationVerifier>.Instance, installDir, version);

            var result = await v.VerifyAsync(CancellationToken.None);

            Assert.Equal(AttestationStatus.NetworkFailure, result.Status);
            Assert.False(halt.IsHalted);
        }
        finally { Directory.Delete(installDir, true); }
    }

    [Fact]
    public async Task Verify_BadSignature_Halts()
    {
        var (installDir, version, _, payload) = MakeFixture();
        try
        {
            var fetcher = new FakeFetcher { Payload = payload };
            var sig = new FakeVerifier { Result = false };
            var hasher = new FakeHasher();
            var halt = new AttestationHaltSignal();

            var v = new AttestationVerifier(fetcher, sig, hasher, halt,
                NullLogger<AttestationVerifier>.Instance, installDir, version);

            var result = await v.VerifyAsync(CancellationToken.None);

            Assert.Equal(AttestationStatus.SignatureInvalid, result.Status);
            Assert.True(halt.IsHalted);
        }
        finally { Directory.Delete(installDir, true); }
    }

    [Fact]
    public async Task Verify_MissingInstallDir_ReturnsConfigError()
    {
        var fetcher = new FakeFetcher();
        var sig = new FakeVerifier();
        var hasher = new FakeHasher();
        var halt = new AttestationHaltSignal();

        var v = new AttestationVerifier(fetcher, sig, hasher, halt,
            NullLogger<AttestationVerifier>.Instance, "/definitely/not/here", "3.13.7");

        var result = await v.VerifyAsync(CancellationToken.None);
        Assert.Equal(AttestationStatus.ConfigurationError, result.Status);
    }

    [Fact]
    public async Task Verify_VersionMismatch_ReturnsConfigError()
    {
        var (installDir, _, _, payload) = MakeFixture();
        try
        {
            var fetcher = new FakeFetcher { Payload = payload };
            var sig = new FakeVerifier { Result = true };
            var hasher = new FakeHasher();
            var halt = new AttestationHaltSignal();

            var v = new AttestationVerifier(fetcher, sig, hasher, halt,
                NullLogger<AttestationVerifier>.Instance, installDir, "3.14.0");

            var result = await v.VerifyAsync(CancellationToken.None);
            Assert.Equal(AttestationStatus.ConfigurationError, result.Status);
            Assert.Contains("version_mismatch", result.Reason);
        }
        finally { Directory.Delete(installDir, true); }
    }
}
