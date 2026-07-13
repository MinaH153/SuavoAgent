using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Security;
using Xunit;

namespace SuavoAgent.Setup.Tests.Security;

public sealed class PioneerRxApprovalBootstrapRequestWriterTests
{
    [Fact]
    public void Queue_WritesExactBoundedConsentReceiptDigestAndUtcInstant()
    {
        var root = TempRoot();
        try
        {
            var path = Path.Combine(root, "nested", "bootstrap-request.json");
            const string consent = "{\"schemaVersion\":\"2.0\",\"accepted\":true}";
            var now = new DateTimeOffset(2026, 7, 12, 16, 30, 45, TimeSpan.FromHours(-7));

            var returned = PioneerRxApprovalBootstrapRequestWriter.Queue(
                "S-1-5-21-100-200-300-400",
                consent,
                path,
                now);

            Assert.Equal(path, returned);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var rootElement = document.RootElement;
            Assert.Equal(
                PioneerRxApprovalBootstrapContract.SchemaVersion,
                rootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                "S-1-5-21-100-200-300-400",
                rootElement.GetProperty("approvedBySid").GetString());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(consent)))
                    .ToLowerInvariant(),
                rootElement.GetProperty("consentReceiptSha256").GetString());
            Assert.Equal(
                now.UtcDateTime.ToString(
                    PioneerRxProcessApprovalContract.UtcTimestampFormat,
                    System.Globalization.CultureInfo.InvariantCulture),
                rootElement.GetProperty("requestedAtUtc").GetString());
            Assert.Equal(4, rootElement.EnumerateObject().Count());
            Assert.Empty(Directory.EnumerateFiles(
                Path.GetDirectoryName(path)!,
                ".bootstrap-request.json.*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Queue_AtomicallyReplacesOnlyTheRequestedFile()
    {
        var root = TempRoot();
        try
        {
            var path = Path.Combine(root, "bootstrap-request.json");
            File.WriteAllText(path, "stale");

            PioneerRxApprovalBootstrapRequestWriter.Queue(
                "S-1-5-18",
                "{\"accepted\":true}",
                path,
                DateTimeOffset.UnixEpoch);

            Assert.NotEqual("stale", File.ReadAllText(path));
            Assert.Single(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("S-2-5-18")]
    [InlineData("s-1-5-18")]
    [InlineData("S-1-")]
    [InlineData("S-1-5-x")]
    [InlineData("S-1-5--18")]
    [InlineData("S-1-5-18 ")]
    public void Queue_RejectsNonCanonicalApproverSid(string? sid)
    {
        var path = Path.Combine(TempRoot(), "request.json");
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                PioneerRxApprovalBootstrapRequestWriter.Queue(
                    sid!,
                    "{\"accepted\":true}",
                    path));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Queue_RejectsMissingConsentEvidence(string consent)
    {
        var path = Path.Combine(TempRoot(), "request.json");
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                PioneerRxApprovalBootstrapRequestWriter.Queue(
                    "S-1-5-18",
                    consent,
                    path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Queue_EnforcesUtf8ByteLimitRatherThanCharacterCount()
    {
        var root = TempRoot();
        try
        {
            var oversized = new string('\u00e9', 32 * 1024 + 1);
            Assert.True(oversized.Length < 64 * 1024);
            Assert.True(Encoding.UTF8.GetByteCount(oversized) > 64 * 1024);

            Assert.Throws<InvalidDataException>(() =>
                PioneerRxApprovalBootstrapRequestWriter.Queue(
                    "S-1-5-18",
                    oversized,
                    Path.Combine(root, "request.json")));
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Queue_CleansTemporaryFileWhenAtomicReplacementFails()
    {
        var root = TempRoot();
        var destinationDirectory = Path.Combine(root, "request.json");
        Directory.CreateDirectory(destinationDirectory);
        try
        {
            Assert.ThrowsAny<IOException>(() =>
                PioneerRxApprovalBootstrapRequestWriter.Queue(
                    "S-1-5-18",
                    "{\"accepted\":true}",
                    destinationDirectory));

            Assert.Empty(Directory.EnumerateFiles(root, ".request.json.*.tmp"));
            Assert.True(Directory.Exists(destinationDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Queue_RejectsPathWithoutParentDirectory()
    {
        Assert.Throws<InvalidDataException>(() =>
            PioneerRxApprovalBootstrapRequestWriter.Queue(
                "S-1-5-18",
                "{\"accepted\":true}",
                string.Empty));
    }

    private static string TempRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "suavo-bootstrap-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
