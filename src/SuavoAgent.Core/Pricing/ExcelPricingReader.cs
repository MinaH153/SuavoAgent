using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Reads an Excel file and returns (rowIndex, ndc) pairs for every data row.
/// Finds exactly one configured NDC identity column by normalized, case-insensitive equality.
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
            var header = FindHeaderRow(ws, ndcColumnHint, lastRow, lastCol);
            if (!header.Ok)
                return ReadResult.Fail(header.ErrorCode ?? "ndc_identity_column_missing");
            var headerRow = header.HeaderRow;
            var ndcCol = header.NdcColumn;

            // A workbook baseline is admitted only when it is explicitly per-unit. PioneerRx's
            // "Acquisition Cost" on the Top-500 report is aggregate spend for the reporting
            // period; treating it as a per-unit baseline fabricates savings by orders of
            // magnitude. Keep that aggregate column out of BaselineCostPerUnit entirely.
            var baselineCol = string.IsNullOrWhiteSpace(baselineCostColumnHint)
                ? -1
                : FindExplicitPerUnitBaselineColumn(
                    ws, headerRow, baselineCostColumnHint!, lastCol);
            var quantityCol = string.IsNullOrWhiteSpace(quantityColumnHint)
                ? -1 : FindColumnInHeaderBlock(ws, headerRow, quantityColumnHint!, lastCol);

            var rows = new List<NdcRow>();
            var invalid = new List<InvalidNdcRow>();
            for (int r = headerRow + 1; r <= lastRow; r++)
            {
                // Printed PioneerRx workbooks repeat the report preamble and footer on every
                // physical page. They are presentation furniture, not failed drug rows. Admit
                // only exact copies of a row above the detected header, or the narrow two-cell
                // printed-footer contract; any extra populated report cell still requires review.
                if (IsExactPreambleCopy(ws, headerRow, r, lastCol) ||
                    IsPrintedPageFooter(ws, r, lastCol))
                    continue;

                var raw = ws.Cell(r, ndcCol).GetString()?.Trim();
                if (string.IsNullOrEmpty(raw))
                {
                    // A truly empty worksheet row is harmless. A populated
                    // report row with no NDC is not: silently dropping it makes
                    // the batch look complete while an operator-visible drug
                    // was never reviewed. Only structural report fields are
                    // considered so page furniture/footer text stays excluded.
                    if (HasMeaningfulReportData(ws, headerRow, r, lastCol, ndcCol))
                        invalid.Add(new InvalidNdcRow(
                            r, "", "blank_ndc_on_populated_row"));
                    continue;
                }
                if (NormalizeHeader(raw).Equals(
                        NormalizeHeader(ndcColumnHint), StringComparison.OrdinalIgnoreCase) &&
                    IsExactHeaderCopy(ws, headerRow, r, lastCol))
                    continue; // PioneerRx repeats the exact report header at printed page breaks.

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
                    "core.excel_pricing_reader.invalid_rows count={Count}",
                    invalid.Count);

            return ReadResult.Ok(rows, invalid, ndcCol, headerRow);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "core.excel_pricing_reader.failed exception_type={ExceptionType}",
                ex.GetType().Name);
            return ReadResult.Fail("Excel read failed");
        }
    }

    /// <summary>Rows to scan from the top when locating the header (covers the preamble
    /// on real PMS exports without risking a mid-sheet false match).</summary>
    private const int MaxHeaderScanRows = 25;

    /// <summary>
    /// The first row carrying an NDC identity header is the candidate header. It is admitted only
    /// when exactly one identity column exists and it exactly matches the configured header.
    /// </summary>
    private static HeaderLookup FindHeaderRow(
        IXLWorksheet ws, string ndcHint, int lastRow, int lastCol)
    {
        var configured = NormalizeHeader(ndcHint);
        if (!IsNdcIdentityHeader(configured))
            return HeaderLookup.Fail("ndc_identity_hint_invalid");

        var scanTo = Math.Min(lastRow, MaxHeaderScanRows);
        for (int r = 1; r <= scanTo; r++)
        {
            var identityColumns = Enumerable.Range(1, lastCol)
                .Where(column => IsNdcIdentityHeader(
                    NormalizeHeader(ws.Cell(r, column).GetString() ?? "")))
                .ToArray();
            if (identityColumns.Length == 0) continue;
            if (identityColumns.Length != 1)
                return HeaderLookup.Fail("ndc_identity_columns_ambiguous");

            var ndcCol = identityColumns[0];
            var actual = NormalizeHeader(ws.Cell(r, ndcCol).GetString() ?? "");
            if (!actual.Equals(configured, StringComparison.OrdinalIgnoreCase))
                return HeaderLookup.Fail("ndc_identity_header_mismatch");
            return HeaderLookup.Success(r, ndcCol);
        }
        return HeaderLookup.Fail("ndc_identity_column_missing");
    }

    private readonly record struct HeaderLookup(
        bool Ok,
        int HeaderRow,
        int NdcColumn,
        string? ErrorCode)
    {
        internal static HeaderLookup Success(int row, int column) =>
            new(true, row, column, null);

        internal static HeaderLookup Fail(string code) =>
            new(false, -1, -1, code);
    }

    private static bool IsNdcIdentityHeader(string value) =>
        value.Equals("NDC", StringComparison.OrdinalIgnoreCase)
        || value.Equals("NDC11", StringComparison.OrdinalIgnoreCase);

    private static int FindColumnInRow(IXLWorksheet ws, int row, string hint, int lastCol)
    {
        var normalizedHint = NormalizeHeader(hint);
        for (int c = 1; c <= lastCol; c++)
        {
            var header = NormalizeHeader(ws.Cell(row, c).GetString() ?? "");
            if (header.Contains(normalizedHint, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return -1;
    }

    private static string NormalizeHeader(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private const int MaxStackedHeaderRows = 4;

    private static int FindColumnInHeaderBlock(
        IXLWorksheet ws, int headerRow, string hint, int lastCol)
    {
        var firstRow = Math.Max(1, headerRow - MaxStackedHeaderRows + 1);
        for (var row = headerRow; row >= firstRow; row--)
        {
            var column = FindColumnInRow(ws, row, hint, lastCol);
            if (column != -1) return column;
        }
        return -1;
    }

    private static int FindExplicitPerUnitBaselineColumn(
        IXLWorksheet ws,
        int headerRow,
        string hint,
        int lastCol)
    {
        var normalizedHint = NormalizeHeader(hint);
        if (!IsExplicitPerUnitBaselineHeader(normalizedHint))
            return -1;

        var firstRow = Math.Max(1, headerRow - MaxStackedHeaderRows + 1);
        for (var row = headerRow; row >= firstRow; row--)
        {
            for (var column = 1; column <= lastCol; column++)
            {
                var actual = NormalizeHeader(ws.Cell(row, column).GetString() ?? "");
                if (actual.Equals(normalizedHint, StringComparison.OrdinalIgnoreCase) &&
                    IsExplicitPerUnitBaselineHeader(actual))
                    return column;
            }
        }
        return -1;
    }

    private static bool IsExplicitPerUnitBaselineHeader(string header) =>
        header.Equals("Current Cost", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Cost (per unit)", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Best Cost Per Unit", StringComparison.OrdinalIgnoreCase);

    private static bool IsExactHeaderCopy(
        IXLWorksheet ws,
        int headerRow,
        int candidateRow,
        int lastCol)
        => IsExactRowCopy(ws, headerRow, candidateRow, lastCol);

    private static bool IsExactPreambleCopy(
        IXLWorksheet ws,
        int headerRow,
        int candidateRow,
        int lastCol)
    {
        for (var preambleRow = 1; preambleRow < headerRow; preambleRow++)
        {
            if (!HasAnyCellValue(ws, preambleRow, lastCol)) continue;
            if (IsExactRowCopy(ws, preambleRow, candidateRow, lastCol))
                return true;
        }
        return false;
    }

    private static bool IsExactRowCopy(
        IXLWorksheet ws,
        int expectedRow,
        int candidateRow,
        int lastCol)
    {
        for (var column = 1; column <= lastCol; column++)
        {
            var expected = NormalizeHeader(ws.Cell(expectedRow, column).GetString() ?? "");
            var actual = NormalizeHeader(ws.Cell(candidateRow, column).GetString() ?? "");
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool HasAnyCellValue(IXLWorksheet ws, int row, int lastCol)
    {
        for (var column = 1; column <= lastCol; column++)
        {
            if (!string.IsNullOrWhiteSpace(ws.Cell(row, column).GetString()))
                return true;
        }
        return false;
    }

    private const int MaximumPrintedPageNumber = 9999;
    private static readonly Regex PrintedPagePattern = new(
        @"^Page\s+(?<current>[1-9]\d{0,3})\s+of\s+(?<total>[1-9]\d{0,3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly string[] PrintedTimestampFormats =
    {
        "M/d/yyyy",
        "M/d/yy",
        "M/d/yyyy h:mm tt",
        "M/d/yy h:mm tt",
        "M/d/yyyy h:mm:ss tt",
        "M/d/yy h:mm:ss tt",
    };

    private static bool IsPrintedPageFooter(
        IXLWorksheet ws,
        int candidateRow,
        int lastCol)
    {
        var populated = new List<IXLCell>(capacity: 3);
        for (var column = 1; column <= lastCol; column++)
        {
            var cell = ws.Cell(candidateRow, column);
            if (string.IsNullOrWhiteSpace(cell.GetString())) continue;
            populated.Add(cell);
            if (populated.Count > 2) return false;
        }

        if (populated.Count != 2) return false;
        return (IsPrintedTimestamp(populated[0]) && IsPrintedPageCount(populated[1])) ||
               (IsPrintedTimestamp(populated[1]) && IsPrintedPageCount(populated[0]));
    }

    private static bool IsPrintedTimestamp(IXLCell cell)
    {
        if (cell.DataType == XLDataType.DateTime && cell.TryGetValue(out DateTime _))
            return true;
        return DateTime.TryParseExact(
            NormalizeHeader(cell.GetString() ?? ""),
            PrintedTimestampFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out _);
    }

    private static bool IsPrintedPageCount(IXLCell cell)
    {
        var match = PrintedPagePattern.Match(NormalizeHeader(cell.GetString() ?? ""));
        if (!match.Success ||
            !int.TryParse(match.Groups["current"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var current) ||
            !int.TryParse(match.Groups["total"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var total))
            return false;
        return current <= total && total <= MaximumPrintedPageNumber;
    }

    private static bool HasMeaningfulReportData(
        IXLWorksheet ws,
        int headerRow,
        int candidateRow,
        int lastCol,
        int ndcColumn)
    {
        for (var column = 1; column <= lastCol; column++)
        {
            if (column == ndcColumn) continue;
            var header = NormalizeHeader(ws.Cell(headerRow, column).GetString() ?? "");
            if (!IsMeaningfulReportHeader(header)) continue;
            if (!string.IsNullOrWhiteSpace(ws.Cell(candidateRow, column).GetString()))
                return true;
        }
        return false;
    }

    private static bool IsMeaningfulReportHeader(string header) =>
        header.Equals("#", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Drug", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Drug Name", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Generic Name", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Brand Name", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Item", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Strength", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Dosage Form", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Manufacturer", StringComparison.OrdinalIgnoreCase);

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
    /// <summary>1-based row the headers live on (past any preamble). The writer places its
    /// Best Supplier/Cost/Status headers on THIS row so they align with the data columns.</summary>
    public int HeaderRowIndex { get; private init; } = 1;

    public static ReadResult Ok(List<NdcRow> rows, List<InvalidNdcRow> invalid, int ndcCol, int headerRow) =>
        new() { Success = true, Rows = rows, Invalid = invalid, NdcColumnIndex = ndcCol, HeaderRowIndex = headerRow };

    public static ReadResult Fail(string error) =>
        new() { Success = false, Error = error };
}
