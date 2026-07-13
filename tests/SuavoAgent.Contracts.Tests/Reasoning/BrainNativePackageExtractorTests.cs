using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Reasoning;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Reasoning;

public sealed class BrainNativePackageExtractorTests : IDisposable
{
    private static readonly string[] Dlls =
    [
        "ggml-base.dll",
        "ggml-cpu.dll",
        "ggml.dll",
        "llama.dll",
        "llava_shared.dll",
    ];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-native-package-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Official_nupkg_extracts_only_exact_win_x64_noavx_dlls_flat()
    {
        var package = WritePackage(OfficialEntries().Concat(
        [
            Entry("runtimes/win-x64/native/avx2/llama.dll", [201]),
            Entry("runtimes/linux-x64/native/noavx/libllama.so", [202]),
            Entry("icon512.png", [203]),
        ]));
        var destination = Path.Combine(_root, "native");

        var result = await BrainNativePackageExtractor.ExtractAsync(
            package,
            destination,
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            CancellationToken.None);

        Assert.True(result.IsValid, result.Code);
        Assert.True(result.IsOfficialNuGetLayout);
        Assert.Equal(Dlls.Order(), result.NativeFiles!.Select(file => file.Path).Order());
        Assert.Equal(Dlls.Order(), Directory.GetFiles(destination).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetDirectories(destination));
        foreach (var (name, index) in Dlls.Select((name, index) => (name, index)))
            Assert.Equal([checked((byte)(index + 1))], await File.ReadAllBytesAsync(
                Path.Combine(destination, name)));
    }

    [Fact]
    public async Task Cached_official_nupkg_matches_release_pin_and_exact_noavx_inventory()
    {
        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
                           Path.Combine(
                               Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                               ".nuget",
                               "packages");
        var package = Path.Combine(
            packagesRoot,
            "llamasharp.backend.cpu",
            BrainNativePackageExtractor.PackageVersion,
            $"llamasharp.backend.cpu.{BrainNativePackageExtractor.PackageVersion}.nupkg");
        if (!File.Exists(package)) return;

        Assert.Equal(21_485_108, new FileInfo(package).Length);
        await using (var stream = File.OpenRead(package))
            Assert.Equal(
                "47120fed200482ab364b9d225271172ccbf2ac7713ad388e4e7fe7d89fdedb0a",
                Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant());

        var result = await BrainNativePackageExtractor.InspectAsync(
            package,
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            CancellationToken.None);

        Assert.True(result.IsValid, result.Code);
        Assert.True(result.IsOfficialNuGetLayout);
        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ggml-base.dll"] =
                    "14aeff8b0ed3867ea7576d62ca4f6c1a684625e41ebb69b81d31fcbaec1725a8",
                ["ggml-cpu.dll"] =
                    "e12c0d54b9bab856d835a1187b27d5419adb872d06bcc3b2f5ae6e3f12ab0ac0",
                ["ggml.dll"] =
                    "7f2cda6a0435484ed57474feb78af6adf6d352dc4cadf14d1b31f02ab188128d",
                ["llama.dll"] =
                    "b0992bb6631dacb8ad0bc105102388f72a52db805dd7b6d3783381d65ee716f4",
                ["llava_shared.dll"] =
                    "4f03624fcc40346a12e0116d59c75a06e78d6fca390effa5a9ce184ea1eda0cc",
            },
            result.NativeFiles!.ToDictionary(file => file.Path, file => file.Sha256));
    }

    [Fact]
    public async Task Missing_or_extra_selected_nupkg_entry_is_rejected()
    {
        var missing = WritePackage(
            OfficialEntries().Where(entry =>
                entry.Name != BrainNativePackageExtractor.NuGetPrefix + "llava_shared.dll"),
            "missing.nupkg");
        var extra = WritePackage(
            OfficialEntries().Append(Entry(
                BrainNativePackageExtractor.NuGetPrefix + "unreviewed.dll",
                [99])),
            "extra.nupkg");

        var missingResult = await BrainNativePackageExtractor.InspectAsync(
            missing,
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            CancellationToken.None);
        var extraResult = await BrainNativePackageExtractor.InspectAsync(
            extra,
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            CancellationToken.None);

        Assert.Equal("native_package_nuget_selected_entry_missing", missingResult.Code);
        Assert.Equal("native_package_nuget_selected_entry_invalid", extraResult.Code);
    }

    [Fact]
    public async Task Wrong_package_identity_or_missing_repository_signature_is_rejected()
    {
        var wrongIdentity = WritePackage(
            OfficialEntries().Select(entry => entry.Name == "LLamaSharp.Backend.Cpu.nuspec"
                ? Entry(entry.Name, Nuspec("LLamaSharp.Backend.Cpu", "0.25.0"))
                : entry),
            "wrong.nupkg");
        var unsigned = WritePackage(
            OfficialEntries().Where(entry => entry.Name != ".signature.p7s"),
            "unsigned.nupkg");

        var wrongResult = await BrainNativePackageExtractor.InspectAsync(
            wrongIdentity,
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            CancellationToken.None);
        var unsignedResult = await BrainNativePackageExtractor.InspectAsync(
            unsigned,
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            CancellationToken.None);

        Assert.Equal("native_package_nuget_identity_invalid", wrongResult.Code);
        Assert.Equal("native_package_nuget_identity_invalid", unsignedResult.Code);
    }

    [Fact]
    public async Task Official_layout_requires_exact_signed_package_kind_and_new_path_rejects_legacy()
    {
        var official = WritePackage(OfficialEntries(), "kind.nupkg");
        var legacy = WritePackage(
            Dlls.Take(4).Select((name, index) => Entry(name, [checked((byte)index)])),
            "kind-legacy.zip");

        var wrongKind = await BrainNativePackageExtractor.InspectAsync(
            official,
            "nuget-llamasharp-backend-cpu-avx2-v1",
            CancellationToken.None);
        var legacyOnNewPath = await BrainNativePackageExtractor.InspectAsync(
            legacy,
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            CancellationToken.None);

        Assert.Equal("native_package_kind_invalid", wrongKind.Code);
        Assert.Equal("native_package_official_layout_required", legacyOnNewPath.Code);
    }

    [Fact]
    public async Task Duplicate_traversal_and_archive_symlink_entries_are_rejected()
    {
        var duplicate = WritePackage(
            OfficialEntries().Append(Entry(
                BrainNativePackageExtractor.NuGetPrefix + "LLAMA.DLL",
                [88])),
            "duplicate.nupkg");
        var traversal = WritePackage(
            OfficialEntries().Append(Entry("../escape.dll", [89])),
            "traversal.nupkg");
        var symlink = WritePackage(
            OfficialEntries().Append(Symlink("ignored-link", "outside")),
            "symlink.nupkg");

        Assert.Equal(
            "native_package_duplicate_entry",
            (await BrainNativePackageExtractor.InspectAsync(
                duplicate,
                BrainNativePackageExtractor.OfficialNuGetPackageKind,
                CancellationToken.None)).Code);
        Assert.Equal(
            "native_package_entry_path_invalid",
            (await BrainNativePackageExtractor.InspectAsync(
                traversal,
                BrainNativePackageExtractor.OfficialNuGetPackageKind,
                CancellationToken.None)).Code);
        Assert.Equal(
            "native_package_reparse_entry",
            (await BrainNativePackageExtractor.InspectAsync(
                symlink,
                BrainNativePackageExtractor.OfficialNuGetPackageKind,
                CancellationToken.None)).Code);
    }

    [Fact]
    public async Task Excessive_entry_count_is_rejected_as_zip_bomb_before_extraction()
    {
        var filler = Enumerable.Range(0, BrainNativePackageExtractor.MaxArchiveEntries)
            .Select(index => Entry($"metadata/{index:D4}.txt", [1]));
        var package = WritePackage(OfficialEntries().Concat(filler), "bomb.nupkg");
        var destination = Path.Combine(_root, "bomb-native");

        var result = await BrainNativePackageExtractor.ExtractAsync(
            package,
            destination,
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            CancellationToken.None);

        Assert.Equal("native_package_entry_count_invalid", result.Code);
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    [Fact]
    public async Task Exact_legacy_flat_package_remains_compatible_but_arbitrary_file_does_not()
    {
        var legacy = WritePackage(
            Dlls.Take(4).Select((name, index) => Entry(name, [checked((byte)index)])),
            "legacy.zip");
        var arbitrary = WritePackage(
            Dlls.Take(4).Select((name, index) => Entry(name, [checked((byte)index)]))
                .Append(Entry("plugin.dll", [9])),
            "arbitrary.zip");

        var legacyResult = await BrainNativePackageExtractor.InspectLegacyFlatAsync(
            legacy,
            CancellationToken.None);
        var arbitraryResult = await BrainNativePackageExtractor.InspectLegacyFlatAsync(
            arbitrary,
            CancellationToken.None);

        Assert.True(legacyResult.IsValid, legacyResult.Code);
        Assert.False(legacyResult.IsOfficialNuGetLayout);
        Assert.Equal(4, legacyResult.NativeFiles!.Count);
        Assert.Equal("native_package_legacy_layout_invalid", arbitraryResult.Code);
    }

    [Fact]
    public async Task Reparse_extraction_root_is_rejected_without_touching_its_target()
    {
        var package = WritePackage(OfficialEntries(), "reparse.nupkg");
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is
                   UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var result = await BrainNativePackageExtractor.ExtractAsync(
            package,
            link,
            BrainNativePackageExtractor.OfficialNuGetPackageKind,
            CancellationToken.None);

        Assert.Equal("native_package_target_invalid", result.Code);
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    private string WritePackage(IEnumerable<ArchiveEntry> entries, string name = "backend.nupkg")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var value in entries)
        {
            var entry = archive.CreateEntry(value.Name, CompressionLevel.Fastest);
            if (value.ExternalAttributes is int attributes)
                entry.ExternalAttributes = attributes;
            using var output = entry.Open();
            output.Write(value.Bytes);
        }
        return path;
    }

    private static IEnumerable<ArchiveEntry> OfficialEntries() =>
        new[]
        {
            Entry("LLamaSharp.Backend.Cpu.nuspec", Nuspec(
                BrainNativePackageExtractor.PackageId,
                BrainNativePackageExtractor.PackageVersion)),
            Entry(".signature.p7s", [42, 43, 44]),
        }.Concat(Dlls.Select((name, index) => Entry(
            BrainNativePackageExtractor.NuGetPrefix + name,
            [checked((byte)(index + 1))])));

    private static byte[] Nuspec(string id, string version) => Encoding.UTF8.GetBytes($$"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd">
          <metadata>
            <id>{{id}}</id>
            <version>{{version}}</version>
            <license type="expression">MIT</license>
          </metadata>
        </package>
        """);

    private static ArchiveEntry Entry(string name, byte[] bytes) => new(name, bytes, null);

    private static ArchiveEntry Symlink(string name, string target) => new(
        name,
        Encoding.UTF8.GetBytes(target),
        (0xA000 | 0x1FF) << 16);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed record ArchiveEntry(string Name, byte[] Bytes, int? ExternalAttributes);
}
