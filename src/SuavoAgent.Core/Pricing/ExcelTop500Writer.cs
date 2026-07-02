using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Writes a generated top-dispensed worklist to an .xlsx shaped exactly like Nadim's PioneerRx
/// "Top 500 Most Dispensed Rx Items" export: a header row <c>Drug | Strength | NDC | Total
/// Dispensed</c> followed by one row per item, ranked as supplied. This is the file the pricing
/// loop then consumes (<see cref="ExcelPricingReader"/> finds the NDC column and prices each row),
/// so generate → price → write-back runs without the manual report-export step.
/// </summary>
public sealed class ExcelTop500Writer
{
    public const string DrugHeader = "Drug";
    public const string StrengthHeader = "Strength";
    public const string NdcHeader = "NDC";
    public const string TotalDispensedHeader = "Total Dispensed";

    private readonly ILogger<ExcelTop500Writer> _logger;

    public ExcelTop500Writer(ILogger<ExcelTop500Writer> logger) => _logger = logger;

    /// <summary>
    /// Writes <paramref name="rows"/> to <paramref name="path"/> (overwrites). Returns true on
    /// success; logs and returns false on any I/O error (never throws to the caller).
    /// </summary>
    public bool Write(string path, IReadOnlyList<TopDispensedRow> rows)
    {
        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Top Dispensed");

            ws.Cell(1, 1).Value = DrugHeader;
            ws.Cell(1, 2).Value = StrengthHeader;
            ws.Cell(1, 3).Value = NdcHeader;
            ws.Cell(1, 4).Value = TotalDispensedHeader;

            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var row = i + 2;
                ws.Cell(row, 1).Value = r.DrugName;
                ws.Cell(row, 2).Value = r.Strength;
                // Force the NDC to text so a numeric-looking 11-digit NDC keeps leading zeros
                // (e.g. 00093-5124-01 → "00093512401"); a numeric cell would drop them.
                ws.Cell(row, 3).SetValue(r.Ndc).Style.NumberFormat.Format = "@";
                ws.Cell(row, 4).Value = r.TotalDispensed;
            }

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            wb.SaveAs(path);
            _logger.LogInformation("ExcelTop500Writer: wrote {Count} rows", rows.Count);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("ExcelTop500Writer failed ({ErrorType})", ex.GetType().Name);
            return false;
        }
    }
}
