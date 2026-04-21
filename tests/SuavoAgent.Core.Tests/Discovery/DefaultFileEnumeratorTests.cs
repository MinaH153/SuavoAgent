using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Core.Verticals.Pharmacy;
using Xunit;

namespace SuavoAgent.Core.Tests.Discovery;

public class DefaultFileEnumeratorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"suavo_enum_{Guid.NewGuid():N}");

    public DefaultFileEnumeratorTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Enumerate_FindsFilesInProvidedRoots_WithCorrectBucket()
    {
        var desktop = CreateRoot("desktop");
        var downloads = CreateRoot("downloads");
        File.WriteAllText(Path.Combine(desktop, "generics.xlsx"), "x");
        File.WriteAllText(Path.Combine(downloads, "other.xlsx"), "x");

        var enumerator = new DefaultFileEnumerator(new[]
        {
            new EnumerationRoot(desktop, FileLocationBucket.Desktop),
            new EnumerationRoot(downloads, FileLocationBucket.Downloads),
        });

        var results = enumerator.Enumerate(PharmacyPresets.NdcPricingList(), DateTimeOffset.UtcNow);

        Assert.Equal(2, results.Count);
        var byBucket = results.ToDictionary(r => r.Bucket);
        Assert.Equal("generics.xlsx", byBucket[FileLocationBucket.Desktop].FileName);
        Assert.Equal("other.xlsx", byBucket[FileLocationBucket.Downloads].FileName);
    }

    [Fact]
    public void Enumerate_AppliesExtensionFilter()
    {
        var root = CreateRoot("root");
        File.WriteAllText(Path.Combine(root, "keep.xlsx"), "x");
        File.WriteAllText(Path.Combine(root, "skip.txt"), "x");
        File.WriteAllText(Path.Combine(root, "also-keep.csv"), "x");

        var enumerator = new DefaultFileEnumerator(new[]
        {
            new EnumerationRoot(root, FileLocationBucket.Other),
        });

        var results = enumerator.Enumerate(PharmacyPresets.NdcPricingList(), DateTimeOffset.UtcNow);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.FileName == "keep.xlsx");
        Assert.Contains(results, r => r.FileName == "also-keep.csv");
        Assert.DoesNotContain(results, r => r.FileName == "skip.txt");
    }

    [Fact]
    public void Enumerate_SkipsMissingRoot_WithoutThrowing()
    {
        var realRoot = CreateRoot("real");
        File.WriteAllText(Path.Combine(realRoot, "g.xlsx"), "x");

        var enumerator = new DefaultFileEnumerator(new[]
        {
            new EnumerationRoot(Path.Combine(_tempDir, "does-not-exist"), FileLocationBucket.Desktop),
            new EnumerationRoot(realRoot, FileLocationBucket.Other),
        });

        var results = enumerator.Enumerate(PharmacyPresets.NdcPricingList(), DateTimeOffset.UtcNow);
        Assert.Single(results);
    }

    [Fact]
    public void Enumerate_OnlyTopLevel_IgnoresSubdirectories()
    {
        var root = CreateRoot("root");
        var sub = Path.Combine(root, "nested");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(root, "top.xlsx"), "x");
        File.WriteAllText(Path.Combine(sub, "deep.xlsx"), "x");

        var enumerator = new DefaultFileEnumerator(new[]
        {
            new EnumerationRoot(root, FileLocationBucket.Desktop),
        });

        var results = enumerator.Enumerate(PharmacyPresets.NdcPricingList(), DateTimeOffset.UtcNow);
        Assert.Single(results);
        Assert.Equal("top.xlsx", results[0].FileName);
    }

    [Fact]
    public void Enumerate_PopulatesFilenameSizeModified()
    {
        var root = CreateRoot("root");
        var path = Path.Combine(root, "g.xlsx");
        File.WriteAllText(path, "hello");
        var enumerator = new DefaultFileEnumerator(new[]
        {
            new EnumerationRoot(root, FileLocationBucket.Desktop),
        });

        var c = enumerator.Enumerate(PharmacyPresets.NdcPricingList(), DateTimeOffset.UtcNow).Single();

        Assert.Equal(path, c.AbsolutePath);
        Assert.Equal("g.xlsx", c.FileName);
        Assert.True(c.SizeBytes > 0);
        Assert.True(c.LastModifiedUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Null(c.LastOpenedUtc); // Default enumerator doesn't have MRU signal yet
    }

    private string CreateRoot(string name)
    {
        var p = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }
}
