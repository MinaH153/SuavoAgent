using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Security.Cryptography;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// The AVX2 native-libs variant selection (v3.60). The provisioner prefers the AVX2 build when the CPU
/// supports it AND an AVX2 zip is configured, else NOAVX — and uses a `.variant` marker so a box can be
/// upgraded noavx → avx2 without a reinstall while NOT needlessly re-downloading a legacy (unmarked)
/// noavx box. The download/extract path itself is network I/O and exercised on the live box, not here.
/// </summary>
public sealed class NativeLibProvisionerVariantTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "suavo-nlp-test-" + Guid.NewGuid().ToString("N"));
    private static readonly string[] RequiredDlls = { "llama.dll", "ggml.dll", "ggml-base.dll", "ggml-cpu.dll" };

    private NativeLibProvisioner Make(bool withAvx2)
    {
        var r = new ReasoningOptions
        {
            Enabled = true,
            NativeLibraryPath = _dir,
            NativeLibsUrl = "https://example/noavx.zip",
            NativeLibsSha256 = new string('a', 64),
        };
        if (withAvx2)
        {
            r.NativeLibsUrlAvx2 = "https://example/avx2.zip";
            r.NativeLibsSha256Avx2 = new string('b', 64);
        }
        var opts = Options.Create(new AgentOptions { Reasoning = r });
        return new NativeLibProvisioner(opts, NullLogger<NativeLibProvisioner>.Instance);
    }

    private void WriteDlls()
    {
        Directory.CreateDirectory(_dir);
        foreach (var d in RequiredDlls) File.WriteAllText(Path.Combine(_dir, d), "stub");
    }

    private void WriteMarker(string variant) => File.WriteAllText(Path.Combine(_dir, ".variant"), variant);

    [Fact]
    public void DesiredVariant_is_noavx_when_no_avx2_zip_configured()
    {
        // CPU-independent: the && short-circuits on the missing avx2 URL/SHA, so even an AVX2 host
        // falls back to noavx when no avx2 build is hosted.
        Assert.Equal("noavx", Make(withAvx2: false).DesiredVariant);
    }

    [Fact]
    public void Bare_box_is_not_correct_variant()
    {
        var p = Make(withAvx2: false);
        Assert.False(p.DllsPresent());
        Assert.False(p.CorrectVariantPresent());
    }

    [Fact]
    public void Legacy_unmarked_box_is_correct_when_noavx_desired()
    {
        // A box provisioned before the variant marker existed: DLLs present, NO marker. With noavx
        // desired (no avx2 hosted), this must read as correct so we don't pointlessly re-download noavx.
        WriteDlls();
        Assert.True(Make(withAvx2: false).CorrectVariantPresent());
    }

    [Fact]
    public void Noavx_desired_with_avx2_marker_is_wrong_variant()
    {
        WriteDlls();
        WriteMarker("avx2");
        Assert.False(Make(withAvx2: false).CorrectVariantPresent());
    }

    [Fact]
    public void Unsigned_avx2_variant_can_never_be_selected()
    {
        var p = Make(withAvx2: true);
        Assert.Equal("noavx", p.DesiredVariant);
    }

    [Theory]
    [InlineData("noavx", "https://example/noavx.zip", 'a')]
    [InlineData("avx2", "https://example/avx2.zip", 'b')]
    public void VariantResolverReturnsOnlyTheRequestedConfiguredTuple(
        string variant,
        string expectedUrl,
        char expectedHashCharacter)
    {
        var provisioner = Make(withAvx2: true);
        var method = typeof(NativeLibProvisioner).GetMethod(
            "ResolveVariant", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ResolveVariant missing.");
        var result = method.Invoke(provisioner, [variant])!;

        Assert.Equal(expectedUrl, result.GetType().GetField("Item1")!.GetValue(result));
        Assert.Equal(new string(expectedHashCharacter, 64),
            result.GetType().GetField("Item2")!.GetValue(result));
    }

    [Fact]
    public async Task DownloadEntryRejectsUnsignedPublisherBeforeNetworkOrExtraction()
    {
        var provisioner = Make(withAvx2: false);
        var method = typeof(NativeLibProvisioner).GetMethod(
            "TryDownloadAndExtractAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TryDownloadAndExtractAsync missing.");

        await Assert.IsAssignableFrom<Task>(method.Invoke(provisioner, [
            "noavx", "https://example.invalid/native.package", new string('a', 64),
            CancellationToken.None]));

        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task NativePackageHashHelperStreamsExactFileBytes()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "native.package");
        var bytes = new byte[] { 1, 3, 5, 7, 9 };
        await File.WriteAllBytesAsync(path, bytes);
        var method = typeof(NativeLibProvisioner).GetMethod(
            "ComputeSha256Async", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ComputeSha256Async missing.");

        var actual = await Assert.IsAssignableFrom<Task<string>>(
            method.Invoke(null, [path, CancellationToken.None]));

        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), actual);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}
