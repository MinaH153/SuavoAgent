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
            // Must remain inside the native workbook policy's approved sheet-name contract.
            var ws = wb.Worksheets.Add("Top 500");

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
            _logger.LogError(
                "core.excel_top500_writer.failed exception_type={ExceptionType}",
                ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Publishes a new generated input in one same-directory move. Existing command output is
    /// never overwritten; recovery must validate and reuse it instead of silently replacing it.
    /// </summary>
    public bool WriteAtomically(string path, IReadOnlyList<TopDispensedRow> rows)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.tmp.xlsx");
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(path)) return false;
            if (!Write(tempPath, rows)) return false;
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.Open,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            {
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: false);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "core.excel_top500_writer.atomic_publish_failed exception_type={ExceptionType}",
                exception.GetType().Name);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "core.excel_top500_writer.temp_cleanup_failed exception_type={ExceptionType}",
                    exception.GetType().Name);
            }
        }
    }
}
