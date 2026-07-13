using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public sealed class HttpModelProvisionerBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "suavo-model-boundary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PublicVerificationRejectsUnsignedPublisherBeforeFileTrust()
    {
        var path = Path.Combine(_root, "model.gguf");
        var provisioner = Create(path, hash: null, size: null);

        var result = await provisioner.VerifyAsync(CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal(path, result.Path);
        Assert.Contains("publisher authorization rejected", result.Reason);
    }

    [Fact]
    public async Task ExistingModelWithoutHashIsPresentButExplicitlyUnchecked()
    {
        var path = WriteModel("unchecked.gguf", [1, 2, 3]);
        var result = await VerifyExistingAsync(Create(path, null, null));

        Assert.True(result.IsValid);
        Assert.Equal(path, result.Path);
        Assert.Null(result.Sha256Actual);
        Assert.Equal("present (hash unchecked)", result.Reason);
    }

    [Fact]
    public async Task ExistingModelSignedSizeMismatchFailsBeforeHashing()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var path = WriteModel("size.gguf", bytes);
        var result = await VerifyExistingAsync(Create(
            path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length + 1));

        Assert.False(result.IsValid);
        Assert.Equal("signed model size mismatch — fail-closed", result.Reason);
        Assert.Null(result.Sha256Actual);
    }

    [Fact]
    public async Task ExistingModelHashMismatchReturnsActualDigestWithoutTrustingFile()
    {
        var bytes = new byte[] { 5, 6, 7, 8 };
        var path = WriteModel("mismatch.gguf", bytes);
        var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var result = await VerifyExistingAsync(Create(path, new string('a', 64), bytes.Length));

        Assert.False(result.IsValid);
        Assert.Equal(actual, result.Sha256Actual);
        Assert.Equal("SHA-256 mismatch — fail-closed", result.Reason);
    }

    [Fact]
    public async Task ExistingModelExactHashAndSizeVerifies()
    {
        var bytes = new byte[] { 9, 10, 11, 12 };
        var path = WriteModel("verified.gguf", bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var result = await VerifyExistingAsync(Create(path, hash.ToUpperInvariant(), bytes.Length));

        Assert.True(result.IsValid);
        Assert.Equal(hash, result.Sha256Actual);
        Assert.Equal("verified", result.Reason);
    }

    [Fact]
    public async Task ExistingModelDisappearingFileIsContainedAsStructuralFailure()
    {
        var path = Path.Combine(_root, "missing.gguf");
        var result = await VerifyExistingAsync(Create(path, new string('b', 64), 0));

        Assert.False(result.IsValid);
        Assert.Null(result.Sha256Actual);
        Assert.StartsWith("model_hash_verification_exception:", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadEntryAlsoRejectsUnsignedPublisherBeforeNetworkOrDiskMutation()
    {
        var path = Path.Combine(_root, "download.gguf");
        var provisioner = Create(path, new string('c', 64), 4, "https://example.invalid/model.gguf");
        var method = typeof(HttpModelProvisioner).GetMethod(
            "TryDownloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryDownloadAsync missing.");
        var task = Assert.IsAssignableFrom<Task>(
            method.Invoke(provisioner, [CancellationToken.None]));

        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task)!;

        Assert.False((bool)result.GetType().GetField("Item1")!.GetValue(result)!);
        Assert.Contains("publisher authorization rejected",
            (string)result.GetType().GetField("Item2")!.GetValue(result)!);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CleanupHelperDeletesExistingTemporaryFileAndIgnoresMissingFile()
    {
        var path = WriteModel("temporary.download", [1]);
        var method = typeof(HttpModelProvisioner).GetMethod(
            "TryDelete", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryDelete missing.");

        method.Invoke(null, [path]);
        method.Invoke(null, [path]);

        Assert.False(File.Exists(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private HttpModelProvisioner Create(
        string path,
        string? hash,
        long? size,
        string? url = null) => new(
        Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                ModelPath = path,
                ModelSha256 = hash,
                ModelSizeBytes = size,
                ModelUrl = url,
            },
        }),
        NullLogger<HttpModelProvisioner>.Instance);

    private string WriteModel(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static async Task<ModelVerificationResult> VerifyExistingAsync(
        HttpModelProvisioner provisioner)
    {
        var method = typeof(HttpModelProvisioner).GetMethod(
            "VerifyExistingAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("VerifyExistingAsync missing.");
        return await Assert.IsAssignableFrom<Task<ModelVerificationResult>>(
            method.Invoke(provisioner, [CancellationToken.None]));
    }
}
