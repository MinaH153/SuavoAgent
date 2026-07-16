using SuavoAgent.Contracts.Vision;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Vision;

public sealed class ReleaseOcrCohortCatalogTests
{
    [Fact]
    public void Release_catalog_contains_only_the_exact_reviewed_native_identity()
    {
        var cohort = Assert.Single(ReleaseOcrCohortCatalog.Approved);

        Assert.Equal(2, cohort.SchemaVersion);
        Assert.Equal(
            "202d82fc7c7d8384df7da57206d5e1f456ccdabd648c46e67cdfaa3a911d4795",
            cohort.BundleSha256);
        Assert.Equal(5_697_774, cohort.BundleSizeBytes);
        Assert.True(ReleaseOcrCohortCatalog.IsWellFormed(cohort));
        Assert.Same(
            cohort,
            ReleaseOcrCohortCatalog.Resolve(cohort.BundleUrl, cohort.BundleSha256));
        Assert.Null(ReleaseOcrCohortCatalog.Resolve(
            cohort.BundleUrl,
            new string('0', 64)));
    }

    [Fact]
    public void Manifest_and_cohort_digests_are_deterministic()
    {
        var cohort = Assert.Single(ReleaseOcrCohortCatalog.Approved);

        Assert.Equal(
            ReleaseOcrCohortCatalog.ComputeCohortId(cohort),
            cohort.CohortId);
        Assert.Equal(
            ReleaseOcrCohortCatalog.ComputeManifestSha256(cohort),
            ReleaseOcrCohortCatalog.ComputeManifestSha256(cohort));
        Assert.Matches(
            "^[0-9a-f]{64}$",
            ReleaseOcrCohortCatalog.ComputeManifestSha256(cohort));
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("C:/Windows/System32/evil.dll")]
    [InlineData("x64//tesseract50.dll")]
    [InlineData("./tesseract50.dll")]
    public void Unsafe_inventory_paths_are_rejected(string value) =>
        Assert.Null(ReleaseOcrCohortCatalog.NormalizeRelativePath(value));
}
