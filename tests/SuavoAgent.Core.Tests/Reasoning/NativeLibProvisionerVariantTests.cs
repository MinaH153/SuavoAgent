using System.Runtime.Intrinsics.X86;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    public void Avx2_desired_needs_explicit_avx2_marker()
    {
        // The avx2 branch only engages where the test CPU actually supports AVX2.
        if (!Avx2.IsSupported) return;
        var p = Make(withAvx2: true);
        Assert.Equal("avx2", p.DesiredVariant);

        WriteDlls(); // unmarked → presumed legacy noavx → needs the upgrade
        Assert.False(p.CorrectVariantPresent());

        WriteMarker("noavx"); // explicitly noavx → still needs the upgrade
        Assert.False(p.CorrectVariantPresent());

        WriteMarker("avx2"); // matches desired → done
        Assert.True(p.CorrectVariantPresent());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}
