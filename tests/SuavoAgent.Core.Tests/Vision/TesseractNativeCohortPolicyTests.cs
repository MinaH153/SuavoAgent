using System.Security.Cryptography;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Vision;
using Xunit;

namespace SuavoAgent.Core.Tests.Vision;

public sealed class TesseractNativeCohortPolicyTests
{
    private static readonly IReadOnlyDictionary<string, byte[]> Files =
        new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["x64/tesseract50.dll"] = new byte[] { 1, 2, 3 },
            ["x64/leptonica-1.82.0.dll"] = new byte[] { 4, 5, 6 },
            ["tessdata/eng.traineddata"] = new byte[] { 7, 8, 9 },
        };

    private static TesseractNativeCohort Cohort() =>
        TesseractNativeCohort.Create(
            "https://assets.example/tesseract.zip",
            new string('a', 64),
            1_024,
            Files.Select(file => new TesseractNativeFile(
                file.Key,
                file.Value.LongLength,
                Sha(file.Value))).ToArray());

    [Fact]
    public void Production_policy_has_reviewed_cohort_and_rejects_forged_config()
    {
        var cohort = Cohort();
        var options = new TesseractOptions
        {
            Enabled = true,
            CohortId = cohort.CohortId,
            BundleSha256 = cohort.BundleSha256,
            ManifestSha256 = TesseractNativeCohortPolicy.ComputeManifestSha256(cohort),
            NativeLibraryPath = Path.Combine(Path.GetTempPath(), cohort.BundleSha256),
        };

        Assert.True(TesseractNativeCohortPolicy.HasReleaseApprovedCohorts);
        Assert.False(TesseractNativeCohortPolicy.VerifyInstalled(options));
    }

    [Fact]
    public void Production_policy_resolves_only_exact_repository_signed_package_identity()
    {
        const string url =
            "https://api.nuget.org/v3-flatcontainer/tesseract/5.2.0/" +
            "tesseract.5.2.0.nupkg";
        const string sha =
            "202d82fc7c7d8384df7da57206d5e1f456ccdabd648c46e67cdfaa3a911d4795";

        var approved = TesseractNativeCohortPolicy.Resolve(url, sha);
        var shared = ReleaseOcrCohortCatalog.Resolve(url, sha);

        Assert.NotNull(approved);
        Assert.NotNull(shared);
        Assert.Equal(shared.CohortId, approved.CohortId);
        Assert.Equal(
            ReleaseOcrCohortCatalog.ComputeManifestSha256(shared),
            TesseractNativeCohortPolicy.ComputeManifestSha256(approved));
        Assert.Equal(2, approved.SchemaVersion);
        Assert.Equal(5_697_774, approved.BundleSizeBytes);
        Assert.EndsWith(
            "tessdata_fast/65727574dfcd264acbb0c3e07860e4e9e9b22185/eng.traineddata",
            approved.TrainedDataSource!.Url,
            StringComparison.Ordinal);
        Assert.Null(TesseractNativeCohortPolicy.Resolve(url, new string('0', 64)));
        Assert.Null(TesseractNativeCohortPolicy.Resolve(url + "?mirror=1", sha));
    }

    [Fact]
    public void Exact_tree_and_manifest_verify_then_file_tamper_fails()
    {
        var cohort = Cohort();
        var root = WriteInstalled(cohort, out var cohortsRoot);
        try
        {
            Assert.True(TesseractNativeCohortPolicy.VerifyInstalledAt(
                root,
                cohortsRoot,
                cohort));

            File.WriteAllBytes(
                Path.Combine(root, "x64", "tesseract50.dll"),
                new byte[] { 0 });

            Assert.False(TesseractNativeCohortPolicy.VerifyInstalledAt(
                root,
                cohortsRoot,
                cohort));
        }
        finally { TryDelete(Path.GetDirectoryName(cohortsRoot)!); }
    }

    [Fact]
    public void Extra_file_and_wrong_content_addressed_directory_are_rejected()
    {
        var cohort = Cohort();
        var root = WriteInstalled(cohort, out var cohortsRoot);
        try
        {
            File.WriteAllText(Path.Combine(root, "extra.dll"), "unexpected");
            Assert.False(TesseractNativeCohortPolicy.VerifyInstalledAt(
                root,
                cohortsRoot,
                cohort));

            File.Delete(Path.Combine(root, "extra.dll"));
            Assert.False(TesseractNativeCohortPolicy.VerifyInstalledAt(
                root,
                Path.GetDirectoryName(cohortsRoot)!,
                cohort));
        }
        finally { TryDelete(Path.GetDirectoryName(cohortsRoot)!); }
    }

    [Fact]
    public void Persisted_manifest_is_bound_to_compiled_inventory()
    {
        var cohort = Cohort();
        var root = WriteInstalled(cohort, out var cohortsRoot);
        try
        {
            var manifest = Path.Combine(root, TesseractNativeCohortPolicy.ManifestFileName);
            var bytes = File.ReadAllBytes(manifest);
            bytes[^1] ^= 1;
            File.WriteAllBytes(manifest, bytes);

            Assert.False(TesseractNativeCohortPolicy.VerifyInstalledAt(
                root,
                cohortsRoot,
                cohort));
        }
        finally { TryDelete(Path.GetDirectoryName(cohortsRoot)!); }
    }

    [Fact]
    public void Loaded_modules_and_traineddata_paths_are_bound_to_exact_cohort()
    {
        var cohort = Cohort();
        var root = WriteInstalled(cohort, out var cohortsRoot);
        try
        {
            var options = Options(root, cohort);
            var modules = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tesseract50.dll"] = Path.Combine(root, "x64", "tesseract50.dll"),
                ["leptonica-1.82.0.dll"] = Path.Combine(
                    root,
                    "x64",
                    "leptonica-1.82.0.dll"),
            };

            Assert.True(TesseractNativeCohortPolicy.VerifyLoadedNativeModulePathsAt(
                options,
                modules,
                cohort,
                cohortsRoot));
            var wrongDataOptions = Options(root, cohort);
            wrongDataOptions.TessdataPath = Path.Combine(Outside(root), "tessdata");
            Assert.False(TesseractNativeCohortPolicy.VerifyLoadedNativeModulePathsAt(
                wrongDataOptions,
                modules,
                cohort,
                cohortsRoot));
            Assert.False(TesseractNativeCohortPolicy.VerifyLoadedNativeModulePathsAt(
                options,
                modules.ToDictionary(
                    item => item.Key,
                    item => item.Key == "tesseract50.dll"
                        ? Path.Combine(Outside(root), item.Key)
                        : item.Value,
                    StringComparer.OrdinalIgnoreCase),
                cohort,
                cohortsRoot));
        }
        finally { TryDelete(Path.GetDirectoryName(cohortsRoot)!); }
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("C:/Windows/System32/evil.dll")]
    [InlineData("tessdata//eng.traineddata")]
    [InlineData("./tesseract50.dll")]
    public void Unsafe_manifest_paths_are_rejected(string path)
    {
        Assert.Null(TesseractNativeCohortPolicy.NormalizeRelativePath(path));
    }

    private static string WriteInstalled(
        TesseractNativeCohort cohort,
        out string cohortsRoot)
    {
        var disposableRoot = Path.Combine(
            Path.GetTempPath(),
            "tessverify-" + Guid.NewGuid().ToString("N"));
        cohortsRoot = Path.Combine(disposableRoot, "cohorts");
        var root = Path.Combine(cohortsRoot, cohort.BundleSha256);
        Directory.CreateDirectory(root);
        foreach (var file in Files)
        {
            var path = TesseractNativeCohortPolicy.SafeEntryPath(root, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }
        File.WriteAllBytes(
            Path.Combine(root, TesseractNativeCohortPolicy.ManifestFileName),
            TesseractNativeCohortPolicy.SerializeManifest(cohort));
        return root;
    }

    private static TesseractOptions Options(
        string root,
        TesseractNativeCohort cohort) => new()
    {
        Enabled = true,
        CohortId = cohort.CohortId,
        BundleSha256 = cohort.BundleSha256,
        ManifestSha256 = TesseractNativeCohortPolicy.ComputeManifestSha256(cohort),
        NativeLibraryPath = root,
        TessdataPath = Path.Combine(root, "tessdata"),
        Language = "eng",
    };

    private static string Outside(string root) =>
        Path.Combine(Path.GetDirectoryName(root)!, "outside");

    private static string Sha(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
