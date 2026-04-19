using OfficeOpenXml;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Reads an Excel file and returns (rowIndex, ndc) pairs for every data row.
/// Finds the NDC column by header name (case-insensitive partial match).
/// </summary>
public sealed class ExcelPricingReader
{
    private readonly ILogger<ExcelPricingReader> _logger;

    public ExcelPricingReader(ILogger<ExcelPricingReader> logger)
    {
        _logger = logger;
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    }

    public ReadResult Read(string path, string ndcColumnHint = "ndc")
    {
        if (!File.Exists(path))
            return ReadResult.Fail($"File not found: {path}");

        try
        {
            using var package = new ExcelPackage(new FileInfo(path));
            var ws = package.Workbook.Worksheets.FirstOrDefault();
            if (ws == null)
                return ReadResult.Fail("Workbook has no worksheets");

            var ndcCol = FindColumn(ws, ndcColumnHint);
            if (ndcCol == -1)
                return ReadResult.Fail($"Could not find column matching '{ndcColumnHint}' in row 1");

            var rows = new List<NdcRow>();
            for (int r = 2; r <= ws.Dimension?.End.Row; r++)
            {
                var raw = ws.Cells[r, ndcCol].Text?.Trim();
                if (string.IsNullOrEmpty(raw)) continue;
                // Normalize NDC: strip hyphens, pad to 11 digits
                var ndc = raw.Replace("-", "").PadLeft(11, '0');
                rows.Add(new NdcRow(r, ndc, raw));
            }

            _logger.LogInformation("ExcelPricingReader: found {Count} NDC rows in {Path}", rows.Count, path);
            return ReadResult.Ok(rows, ndcCol);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExcelPricingReader failed for {Path}", path);
            return ReadResult.Fail(ex.Message);
        }
    }

    private static int FindColumn(ExcelWorksheet ws, string hint)
    {
        if (ws.Dimension == null) return -1;
        for (int c = 1; c <= ws.Dimension.End.Column; c++)
        {
            var header = ws.Cells[1, c].Text?.Trim() ?? "";
            if (header.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return -1;
    }
}

public record NdcRow(int RowIndex, string NdcNormalized, string NdcRaw);

public sealed class ReadResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }
    public IReadOnlyList<NdcRow> Rows { get; private init; } = [];
    public int NdcColumnIndex { get; private init; }

    public static ReadResult Ok(List<NdcRow> rows, int ndcCol) =>
        new() { Success = true, Rows = rows, NdcColumnIndex = ndcCol };

    public static ReadResult Fail(string error) =>
        new() { Success = false, Error = error };
}
