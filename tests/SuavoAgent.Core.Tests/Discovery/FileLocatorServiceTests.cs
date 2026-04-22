using ClosedXML.Excel;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Core.Verticals.Pharmacy;
using Xunit;

namespace SuavoAgent.Core.Tests.Discovery;

/// <summary>
/// End-to-end locator: real enumerator over a temp fake Desktop/Downloads,
/// real sampler over real xlsx files, real scorer, real heuristic-only
/// ranker. The only thing this test doesn't exercise is the LLM ranker
/// (session 3).
/// </summary>
public class FileLocatorServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"suavo_locator_{Guid.NewGuid():N}");

    public FileLocatorServiceTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task Locate_IdealPharmacyCase_ReturnsAutoUseOrRequireConfirm()
    {
        var desktop = Root("desktop");
        var documents = Root("documents");
        CreateNdcExcel(Path.Combine(desktop, "generics_top500.xlsx"), 200);
        CreateGenericExcel(Path.Combine(documents, "old_audit.xlsx"), 800);
        CreateGenericExcel(Path.Combine(documents, "random_notes.xlsx"), 50);

        var locator = BuildLocator(desktop, documents);
        var result = await locator.LocateAsync(PharmacyPresets.NdcPricingList(), DateTimeOffset.UtcNow);

        Assert.NotNull(result.Best);
        Assert.Equal("generics_top500.xlsx", result.Best!.Candidate.Candidate.FileName);
        Assert.True(
            result.Resolution is FileDiscoveryResolution.AutoUse or FileDiscoveryResolution.RequireConfirm,
            $"expected auto/confirm, got {result.Resolution} confidence={result.Best.Confidence:F3}");
        Assert.Equal(RankerTier.Heuristic, result.Best.Tier);
    }

    [Fact]
    public async Task Locate_EmptyFolder_ReturnsNotFound()
    {
        var desktop = Root("desktop");
        var locator = BuildLocator(desktop);

        var result = await locator.LocateAsync(PharmacyPresets.NdcPricingList(), DateTimeOffset.UtcNow);

        Assert.Equal(FileDiscoveryResolution.NotFound, result.Resolution);
        Assert.Null(result.Best);
        Assert.Empty(result.Alternatives);
    }

    [Fact]
    public async Task Locate_AmbiguousCase_ReturnsMultipleAlternatives()
    {
        var desktop = Root("desktop");
        var downloads = Root("downloads");
        // Three files that all look plausible — none should auto-use.
        CreateGenericExcel(Path.Combine(desktop, "generic_list.xlsx"), 200);
        CreateGenericExcel(Path.Combine(downloads, "top_drugs.xlsx"), 200);
        CreateGenericExcel(Path.Combine(desktop, "formulary_notes.xlsx"), 200);

        var locator = BuildLocator(desktop, downloads);
        var result = await locator.LocateAsync(PharmacyPresets.NdcPricingList(), DateTimeOffset.UtcNow);

        Assert.NotNull(result.Best);
        Assert.NotEmpty(result.Alternatives);
        // Best.Confidence is below auto-use because no NDC primary key detected.
        Assert.NotEqual(FileDiscoveryResolution.AutoUse, result.Resolution);
    }

    [Fact]
    public async Task Locate_RankingsIncludeAuditTrail()
    {
        var desktop = Root("desktop");
        CreateNdcExcel(Path.Combine(desktop, "generics.xlsx"), 100);
        var locator = BuildLocator(desktop);

        var result = await locator.LocateAsync(PharmacyPresets.NdcPricingList(), DateTimeOffset.UtcNow);

        Assert.NotNull(result.Best);
        Assert.Equal(RankerTier.Heuristic, result.Best!.Tier);
        Assert.NotNull(result.Best.SignalBreakdown);
        Assert.Contains("heuristic", result.Best.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------

    private FileLocatorService BuildLocator(params string[] rootPaths)
    {
        var roots = rootPaths
            .Select((p, i) => new EnumerationRoot(
                p,
                i == 0 ? FileLocationBucket.Desktop : FileLocationBucket.Documents))
            .ToList();

        return new FileLocatorService(
            enumerator: new DefaultFileEnumerator(roots),
            scorer: new FilenameHeuristicScorer(),
            sampler: new TabularShapeSampler(),
            ranker: new HeuristicOnlyRanker(),
            options: new FileLocatorOptions { SampleDepth = 5 });
    }

    private string Root(string name)
    {
        var p = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void CreateNdcExcel(string path, int dataRows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Generic Name";
        ws.Cell(1, 2).Value = "NDC";
        ws.Cell(1, 3).Value = "Qty";
        for (int i = 0; i < dataRows; i++)
        {
            ws.Cell(i + 2, 1).Value = $"Drug {i}";
            ws.Cell(i + 2, 2).Value = $"55111-{i:D4}-01";
            ws.Cell(i + 2, 3).Value = 30;
        }
        wb.SaveAs(path);
    }

    private static void CreateGenericExcel(string path, int dataRows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = "Note";
        ws.Cell(1, 2).Value = "Amount";
        for (int i = 0; i < dataRows; i++)
        {
            ws.Cell(i + 2, 1).Value = $"Row {i}";
            ws.Cell(i + 2, 2).Value = i * 1.5;
        }
        wb.SaveAs(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }
}
