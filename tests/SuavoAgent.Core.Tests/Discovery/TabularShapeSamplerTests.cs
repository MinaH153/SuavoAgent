using ClosedXML.Excel;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Core.Discovery;
using SuavoAgent.Core.Verticals.Pharmacy;
using Xunit;

namespace SuavoAgent.Core.Tests.Discovery;

public class TabularShapeSamplerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), $"suavo_sampler_{Guid.NewGuid():N}");
    private readonly TabularShapeSampler _sampler = new();

    public TabularShapeSamplerTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task Sample_XlsxWithNdcColumn_DetectsPrimaryKey()
    {
        var path = CreateExcel("generics.xlsx", new[]
        {
            new[] { "Generic Name", "NDC", "Qty" },
            new[] { "Lisinopril 10mg", "55111-0645-01", "30" },
            new[] { "Omeprazole 20mg", "55111064501", "60" },
            new[] { "Metformin 500mg", "0781-5180-01", "90" },
            new[] { "Amoxicillin 500mg", "5511106450", "14" },
        });
        var candidate = MakeCandidate(path);

        var sample = await _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);

        Assert.Null(sample.ErrorMessage);
        var tab = Assert.IsType<TabularShapeSample>(sample.Shape);
        Assert.Equal(4, tab.RowCount);                       // 4 data rows excluding header
        Assert.Equal(1, tab.PrimaryKeyColumnIndex);          // NDC column index 1
        Assert.True(tab.StructureMatchesHints);
        Assert.Contains("NDC", tab.ColumnHeaders);
    }

    [Fact]
    public async Task Sample_XlsxWithoutPrimaryKey_ReportsNoPrimaryKey()
    {
        var path = CreateExcel("invoices.xlsx", new[]
        {
            new[] { "Invoice", "Vendor", "Amount" },
            new[] { "INV-001", "Acme", "100.00" },
            new[] { "INV-002", "Beta", "200.00" },
        });
        var candidate = MakeCandidate(path);

        var sample = await _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);

        var tab = Assert.IsType<TabularShapeSample>(sample.Shape);
        Assert.Equal(TabularShapeSample.NoPrimaryKey, tab.PrimaryKeyColumnIndex);
        Assert.False(tab.StructureMatchesHints);
    }

    [Fact]
    public async Task Sample_Csv_ReadsHeadersAndRows()
    {
        var path = Path.Combine(_tempDir, "generics.csv");
        File.WriteAllLines(path, new[]
        {
            "Generic,NDC,Supplier",
            "Lisinopril,55111-0645-01,",
            "Omeprazole,55111064501,",
            "Metformin,0781-5180-01,",
        });
        var candidate = MakeCandidate(path);

        var sample = await _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);

        var tab = Assert.IsType<TabularShapeSample>(sample.Shape);
        Assert.Equal(3, tab.ColumnHeaders.Count);
        Assert.Equal(3, tab.RowCount);
        Assert.Equal(1, tab.PrimaryKeyColumnIndex);
    }

    [Fact]
    public async Task Sample_UnsupportedExtension_ReturnsError()
    {
        var path = Path.Combine(_tempDir, "file.pdf");
        File.WriteAllText(path, "%PDF-1.4 fake");
        var candidate = MakeCandidate(path);

        var sample = await _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);

        Assert.NotNull(sample.ErrorMessage);
        Assert.Null(sample.Shape);
    }

    [Fact]
    public async Task Sample_NonLocking_AllowsConcurrentRead()
    {
        var path = CreateExcel("concurrent.xlsx", new[]
        {
            new[] { "NDC" },
            new[] { "55111-0645-01" },
        });
        var candidate = MakeCandidate(path);

        // Two concurrent samples — must not trip FileShare.
        var t1 = _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);
        var t2 = _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);
        var results = await Task.WhenAll(t1, t2);

        Assert.All(results, r => Assert.Null(r.ErrorMessage));
    }

    [Fact]
    public async Task Sample_MissingFile_ReturnsErrorMessage()
    {
        var candidate = MakeCandidate(Path.Combine(_tempDir, "ghost.xlsx"));

        var sample = await _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);

        Assert.NotNull(sample.ErrorMessage);
        Assert.Null(sample.Shape);
    }

    [Fact]
    public async Task Sample_LegacyXlsExtension_ReturnsUnsupportedError()
    {
        // ClosedXML only reads OOXML (.xlsx). Legacy OLE2 (.xls) must surface
        // as an unsupported-extension error instead of silently crashing
        // or returning garbage structure. Pack authors are responsible for
        // not advertising .xls in CommonExtensions until we wire a
        // different reader.
        var path = Path.Combine(_tempDir, "legacy.xls");
        File.WriteAllBytes(path, new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }); // OLE2 magic
        var candidate = MakeCandidate(path);

        var sample = await _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);

        Assert.NotNull(sample.ErrorMessage);
        Assert.Null(sample.Shape);
        Assert.Contains("Unsupported extension", sample.ErrorMessage);
    }

    [Fact]
    public async Task Sample_CsvWithQuotedFields_ParsesCorrectly()
    {
        // A drug name with an embedded comma in a quoted field must not
        // shift the NDC column. This is the case that broke the old naive
        // line.Split(',') parser.
        var path = Path.Combine(_tempDir, "quoted.csv");
        File.WriteAllText(
            path,
            "Drug Name,NDC,Supplier\n" +
            "\"Lisinopril, 10mg\",55111-0645-01,Acme\n" +
            "\"Metformin, 500mg\",55111064501,Beta\n" +
            "\"Omeprazole, DR, 20mg\",0781-5180-01,Gamma\n");
        var candidate = MakeCandidate(path);

        var sample = await _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);

        var tab = Assert.IsType<TabularShapeSample>(sample.Shape);
        Assert.Equal(3, tab.ColumnHeaders.Count);
        Assert.Equal("NDC", tab.ColumnHeaders[1]);
        Assert.Equal(1, tab.PrimaryKeyColumnIndex);    // NDC column — not shifted
        Assert.Equal(3, tab.RowCount);
    }

    [Fact]
    public async Task Sample_CsvWithEscapedQuotes_ParsesCorrectly()
    {
        var path = Path.Combine(_tempDir, "escaped_quotes.csv");
        // MinSampleMatches defaults to 3, so include enough rows with
        // valid NDC values for the primary-key detector to fire.
        File.WriteAllText(
            path,
            "Note,NDC\n" +
            "\"He said \"\"hi\"\"\",55111-0645-01\n" +
            "Plain text,55111064501\n" +
            "\"Another \"\"quoted\"\" note\",0781-5180-01\n");
        var candidate = MakeCandidate(path);

        var sample = await _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), CancellationToken.None);

        var tab = Assert.IsType<TabularShapeSample>(sample.Shape);
        Assert.Equal(3, tab.RowCount);
        Assert.Equal(1, tab.PrimaryKeyColumnIndex);
    }

    [Fact]
    public async Task Sample_CancellationPropagates()
    {
        var path = CreateExcel("cancellable.xlsx", new[]
        {
            new[] { "NDC" },
            new[] { "55111-0645-01" },
        });
        var candidate = MakeCandidate(path);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // The sampler must re-throw OperationCanceledException rather than
        // swallowing it into an ErrorMessage — callers rely on OCE to
        // reliably abort the whole discovery.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sampler.SampleAsync(candidate, PharmacyPresets.NdcPricingList(), cts.Token));
    }

    // ---------------------------------------------------------------------

    private string CreateExcel(string name, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var path = Path.Combine(_tempDir, name);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        for (int r = 0; r < rows.Count; r++)
        {
            for (int c = 0; c < rows[r].Count; c++)
            {
                ws.Cell(r + 1, c + 1).Value = rows[r][c];
            }
        }
        wb.SaveAs(path);
        return path;
    }

    private static FileCandidate MakeCandidate(string path)
    {
        var info = new FileInfo(path);
        return new FileCandidate(
            AbsolutePath: info.FullName,
            FileName: info.Name,
            SizeBytes: info.Exists ? info.Length : 0,
            LastModifiedUtc: info.Exists ? info.LastWriteTimeUtc : DateTimeOffset.UtcNow,
            Bucket: FileLocationBucket.Desktop,
            HeuristicScore: 0.0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }
}
