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

    public ReadResult Read(string path, string ndcColumnHint = "ndc")
    {
        if (!File.Exists(path))
            return ReadResult.Fail($"File not found: {path}");

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

            var ndcCol = FindColumn(ws, ndcColumnHint, lastCol);
            if (ndcCol == -1)
                return ReadResult.Fail($"Could not find column matching '{ndcColumnHint}' in row 1");

            var rows = new List<NdcRow>();
            var invalid = new List<InvalidNdcRow>();
            for (int r = 2; r <= lastRow; r++)
            {
                var raw = ws.Cell(r, ndcCol).GetString()?.Trim();
                if (string.IsNullOrEmpty(raw)) continue;

                var outcome = NdcNormalizer.Normalize(raw);
                if (outcome.Ok && outcome.Canonical11 is not null)
                {
                    rows.Add(new NdcRow(r, outcome.Canonical11, raw));
                }
                else
                {
                    invalid.Add(new InvalidNdcRow(r, raw, outcome.Reason ?? "Unknown NDC shape"));
                }
            }

            _logger.LogInformation(
                "ExcelPricingReader: {Valid} NDC rows, {Invalid} unparseable in {Path}",
                rows.Count, invalid.Count, path);

            if (invalid.Count > 0)
                _logger.LogWarning(
                    "ExcelPricingReader: {Count} rows had unparseable NDCs (first 5): {Samples}",
                    invalid.Count,
                    string.Join("; ", invalid.Take(5).Select(i => $"row {i.RowIndex}='{i.NdcRaw}' ({i.Reason})")));

            return ReadResult.Ok(rows, invalid, ndcCol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExcelPricingReader failed for {Path}", path);
            return ReadResult.Fail(ex.Message);
        }
    }

    private static int FindColumn(IXLWorksheet ws, string hint, int lastCol)
    {
        for (int c = 1; c <= lastCol; c++)
        {
            var header = ws.Cell(1, c).GetString()?.Trim() ?? "";
            if (header.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return -1;
    }
}

public record NdcRow(int RowIndex, string NdcNormalized, string NdcRaw);

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
