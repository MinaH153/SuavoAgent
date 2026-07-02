using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>
/// The FULL back-half deliverable, end-to-end: a real "Top 500" export shape (PioneerRx-style preamble
/// ABOVE the header, verified against Nadim's file) → ExcelPricingReader → per-NDC result →
/// ExcelPricingWriter → a filled worklist. Guards the subtle bug the individual reader/writer tests
/// don't: the header the reader DETECTS under the preamble must flow into the writer as headerRow, or
/// the Best Supplier / Best Cost / Status columns land on the wrong row and every result misaligns.
/// </summary>
public sealed class PricingDeliverableRoundTripTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_price_e2e_{Guid.NewGuid():N}");

    public PricingDeliverableRoundTripTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public void Reader_to_writer_round_trip_fills_the_worklist_with_the_header_under_a_preamble()
    {
        var input = Path.Combine(_tempDir, "Top500.xlsx");
        BuildWorklistWithPreamble(input, headerRow: 5);

        var reader = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance);
        var read = reader.Read(input, ndcColumnHint: "ndc");
        Assert.True(read.Success, read.Error);
        Assert.Equal(5, read.HeaderRowIndex);              // detected UNDER the 4-row preamble
        Assert.Equal(3, read.Rows.Count);

        // Price row 6 (Omeprazole DR 40mg) with the real answer; leave the others unpriced (honest NO_MATCH).
        var results = read.Rows.Select(row =>
            row.NdcNormalized == "55111064501"
                ? new SupplierPriceResult("e2e", row.RowIndex, row.NdcNormalized, true, "McKesson", 0.0099m, null)
                : new SupplierPriceResult("e2e", row.RowIndex, row.NdcNormalized, false, null, null, "no_match"))
            .ToList();

        var writer = new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance);
        var write = writer.Write(input, results, headerRow: read.HeaderRowIndex);
        Assert.True(write.Success, write.Error);
        Assert.Equal(1, write.OkRows);
        Assert.Equal(2, write.FailRows);

        // Re-read the OUTPUT and prove the priced row is filled AND the columns aligned to the header row.
        using var wb = new XLWorkbook(write.OutputPath!);
        var ws = wb.Worksheet(1);
        int Col(string name) { for (int c = 1; c <= 30; c++) if (string.Equals(ws.Cell(5, c).GetString().Trim(), name, StringComparison.OrdinalIgnoreCase)) return c; return -1; }
        int sup = Col(PricingJobDefaults.SupplierColumn), cost = Col(PricingJobDefaults.CostColumn), st = Col("Price Lookup Status");
        Assert.True(sup > 0 && cost > 0 && st > 0, "new columns must be created on the detected header row");

        Assert.Equal("McKesson", ws.Cell(6, sup).GetString());
        Assert.Equal(0.0099, ws.Cell(6, cost).GetDouble(), 4);
        Assert.Equal("OK", ws.Cell(6, st).GetString());
        // an unpriced row carries an explicit marker, not a stale/blank supplier
        Assert.Equal("", ws.Cell(7, sup).GetString());
        Assert.NotEqual("OK", ws.Cell(7, st).GetString());
    }

    private static void BuildWorklistWithPreamble(string path, int headerRow)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Top 500");
        ws.Cell(1, 1).Value = "Top 500 Most Dispensed Rx Items";
        ws.Cell(2, 1).Value = "Better Life Pharmacy";
        ws.Cell(3, 1).Value = "Date range: 2025-01-01 to 2025-12-31";
        ws.Cell(headerRow, 1).Value = "#";
        ws.Cell(headerRow, 2).Value = "Drug";
        ws.Cell(headerRow, 3).Value = "NDC";
        var rows = new[]
        {
            ("1", "Omeprazole DR 40mg", "55111-0645-01"),
            ("2", "Atorvastatin 20mg", "68180-0472-01"),
            ("3", "Lisinopril 20mg", "68180-0518-01"),
        };
        var r = headerRow + 1;
        foreach (var (n, d, ndc) in rows)
        {
            ws.Cell(r, 1).Value = n; ws.Cell(r, 2).Value = d; ws.Cell(r, 3).Value = ndc; r++;
        }
        wb.SaveAs(path);
    }
}
