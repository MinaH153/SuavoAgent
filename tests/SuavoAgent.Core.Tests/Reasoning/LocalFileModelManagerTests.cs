using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class LocalFileModelManagerTests
{
    [Fact]
    public async Task Verify_NoPathConfigured_IsInvalid()
    {
        var mgr = New(new ReasoningOptions());
        var result = await mgr.VerifyAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("not configured", result.Reason);
    }

    [Fact]
    public async Task Verify_MissingFile_IsInvalid()
    {
        var mgr = New(new ReasoningOptions { ModelPath = "/does/not/exist.gguf" });
        var result = await mgr.VerifyAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("missing", result.Reason!.ToLowerInvariant());
    }

    [Fact]
    public async Task Verify_FilePresentNoHash_IsValid_WithWarning()
    {
        using var file = new TempFile("test content");
        var mgr = New(new ReasoningOptions { ModelPath = file.Path });

        var result = await mgr.VerifyAsync(CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Contains("unchecked", result.Reason!);
    }

    [Fact]
    public async Task Verify_CorrectHash_IsValid()
    {
        const string content = "deterministic model content";
        using var file = new TempFile(content);
        var expectedHash = Sha256Hex(content);

        var mgr = New(new ReasoningOptions
        {
            ModelPath = file.Path,
            ModelSha256 = expectedHash,
        });

        var result = await mgr.VerifyAsync(CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(expectedHash, result.Sha256Actual);
    }

    [Fact]
    public async Task Verify_HashMismatch_IsInvalid()
    {
        using var file = new TempFile("real content");
        var mgr = New(new ReasoningOptions
        {
            ModelPath = file.Path,
            ModelSha256 = "0000000000000000000000000000000000000000000000000000000000000000",
        });

        var result = await mgr.VerifyAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains("mismatch", result.Reason!.ToLowerInvariant());
        Assert.NotEqual("0000000000000000000000000000000000000000000000000000000000000000",
            result.Sha256Actual);
    }

    [Fact]
    public async Task Verify_HashComparisonIsCaseInsensitive()
    {
        const string content = "content";
        using var file = new TempFile(content);
        var expectedUpper = Sha256Hex(content).ToUpperInvariant();

        var mgr = New(new ReasoningOptions
        {
            ModelPath = file.Path,
            ModelSha256 = expectedUpper,
        });

        var result = await mgr.VerifyAsync(CancellationToken.None);
        Assert.True(result.IsValid);
    }

    // --- helpers -------------------------------------------------------------

    private static LocalFileModelManager New(ReasoningOptions options)
    {
        var agentOpts = new AgentOptions { Reasoning = options };
        return new LocalFileModelManager(
            Options.Create(agentOpts),
            NullLogger<LocalFileModelManager>.Instance);
    }

    private static string Sha256Hex(string s)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class TempFile : IDisposable
    {
        public string Path { get; }
        public TempFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "suavo-model-test-" + Guid.NewGuid().ToString("N") + ".gguf");
            File.WriteAllText(Path, content);
        }
        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }
}
