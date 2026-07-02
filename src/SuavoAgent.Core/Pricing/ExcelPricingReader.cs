using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Reads an Excel file and returns (rowIndex, ndc) pairs for every data row.
/// Finds the NDC column by header name (case-insensitive partial match).
/// Uses ClosedXML (MIT) — EPPlus was removed because its NonCommercial clause
/// would not survive a paid pilot.
/// </summary>
public sealed class ExcelPricingReader
{
    private readonly ILogger<ExcelPricingReader> _logger;

    public ExcelPricingReader(ILogger<ExcelPricingReader> logger)
    {
        _logger = logger;
    }

    public ReadResult Read(
        string path,
        string ndcColumnHint = "ndc",
        string? baselineCostColumnHint = null,
        string? quantityColumnHint = null)
    {
        if (!File.Exists(path))
            return ReadResult.Fail("File not found");

        try
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null)
                return ReadResult.Fail("Workbook has no worksheets");

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastRow < 2 || lastCol < 1)
                return ReadResult.Fail("Worksheet has no data rows");

            // Detect the header row instead of assuming row 1. Real PioneerRx "Top 500
            // Most Dispensed Rx Items" exports carry a title/pharmacy/address/filter preamble
            // ABOVE the header (verified against Nadim's actual sheet: 5 preamble rows, then
            // "# | Drug | Strength | NDC | Total Dispensed | Price"). Assuming row 1 made the
            // reader fail-closed on his real file. Scan a bounded window for the first row that
            // carries the NDC column; that's the header.
            var (headerRow, ndcCol) = FindHeaderRow(ws, ndcColumnHint, lastRow, lastCol);
            if (headerRow == -1)
                return ReadResult.Fail(
                    $"Could not find a header row with a column matching '{ndcColumnHint}' in the first {Math.Min(lastRow, MaxHeaderScanRows)} rows");

            // M1 savings: optionally capture the pharmacist's OWN current cost + dispensed volume
            // columns if the workbook carries them (Nadim's export has "Acquisition Cost" +
            // "Total Dispensed"). Most honest baseline; needs no PMS query. Absent -> -1 -> null.
            var baselineCol = string.IsNullOrWhiteSpace(baselineCostColumnHint)
                ? -1 : FindColumnInRow(ws, headerRow, baselineCostColumnHint!, lastCol);
            var quantityCol = string.IsNullOrWhiteSpace(quantityColumnHint)
                ? -1 : FindColumnInRow(ws, headerRow, quantityColumnHint!, lastCol);

            var rows = new List<NdcRow>();
            var invalid = new List<InvalidNdcRow>();
            for (int r = headerRow + 1; r <= lastRow; r++)
            {
                var raw = ws.Cell(r, ndcCol).GetString()?.Trim();
                if (string.IsNullOrEmpty(raw)) continue;

                var outcome = NdcNormalizer.Normalize(raw);
                if (outcome.Ok && outcome.Canonical11 is not null)
                {
                    rows.Add(new NdcRow(
                        r, outcome.Canonical11, raw,
                        BaselineCostPerUnit: ReadDecimalCell(ws, r, baselineCol),
                        Quantity: ReadDecimalCell(ws, r, quantityCol)));
                }
                else
                {
                    invalid.Add(new InvalidNdcRow(r, raw, outcome.Reason ?? "Unknown NDC shape"));
                }
            }

            _logger.LogInformation(
                "ExcelPricingReader: {Valid} NDC rows, {Invalid} unparseable",
                rows.Count, invalid.Count);

            if (invalid.Count > 0)
                _logger.LogWarning(
                    "ExcelPricingReader: {Count} rows had unparseable NDCs (first 5): {Samples}",
                    invalid.Count,
                    string.Join("; ", invalid.Take(5).Select(i => $"row {i.RowIndex}='{i.NdcRaw}' ({i.Reason})")));

            return ReadResult.Ok(rows, invalid, ndcCol);
        }
        catch (Exception ex)
        {
            _logger.LogError("ExcelPricingReader failed ({ErrorType})", ex.GetType().Name);
            return ReadResult.Fail("Excel read failed");
        }
    }

    /// <summary>Rows to scan from the top when locating the header (covers the preamble
    /// on real PMS exports without risking a mid-sheet false match).</summary>
    private const int MaxHeaderScanRows = 25;

    /// <summary>
    /// The first row (within the scan window) that contains a column whose header matches
    /// the NDC hint — that row IS the header. Returns (headerRow, ndcCol) or (-1, -1).
    /// </summary>
    private static (int headerRow, int ndcCol) FindHeaderRow(
        IXLWorksheet ws, string ndcHint, int lastRow, int lastCol)
    {
        var scanTo = Math.Min(lastRow, MaxHeaderScanRows);
        for (int r = 1; r <= scanTo; r++)
        {
            var ndcCol = FindColumnInRow(ws, r, ndcHint, lastCol);
            if (ndcCol != -1) return (r, ndcCol);
        }
        return (-1, -1);
    }

    private static int FindColumnInRow(IXLWorksheet ws, int row, string hint, int lastCol)
    {
        for (int c = 1; c <= lastCol; c++)
        {
            var header = ws.Cell(row, c).GetString()?.Trim() ?? "";
            if (header.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return -1;
    }

    /// <summary>Reads a positive decimal from a cell; null for an absent column, blank, or
    /// non-positive value (a blank/zero baseline must not produce a phantom savings).</summary>
    private static decimal? ReadDecimalCell(IXLWorksheet ws, int row, int col)
    {
        if (col < 1) return null;
        var cell = ws.Cell(row, col);
        if (cell.TryGetValue(out decimal d)) return d > 0 ? d : null;
        var raw = cell.GetString()?.Trim();
        if (!string.IsNullOrEmpty(raw) &&
            decimal.TryParse(raw.TrimStart('$'), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed > 0 ? parsed : null;
        return null;
    }
}

public record NdcRow(
    int RowIndex,
    string NdcNormalized,
    string NdcRaw,
    decimal? BaselineCostPerUnit = null,
    decimal? Quantity = null);

public record InvalidNdcRow(int RowIndex, string NdcRaw, string Reason);

public sealed class ReadResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }
    public IReadOnlyList<NdcRow> Rows { get; private init; } = [];
    public IReadOnlyList<InvalidNdcRow> Invalid { get; private init; } = [];
    public int NdcColumnIndex { get; private init; }

    public static ReadResult Ok(List<NdcRow> rows, List<InvalidNdcRow> invalid, int ndcCol) =>
        new() { Success = true, Rows = rows, Invalid = invalid, NdcColumnIndex = ndcCol };

    public static ReadResult Fail(string error) =>
        new() { Success = false, Error = error };
}
