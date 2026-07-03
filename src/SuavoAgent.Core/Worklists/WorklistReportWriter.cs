using ClosedXML.Excel;

namespace SuavoAgent.Core.Worklists;

/// <summary>
/// Shared writer for the inventory/dispensing WORKLIST features (reorder, invoice-recon, short-dated,
/// will-call). Generalizes <c>ExcelPricingWriter</c>: given a sheet name, a header row, and rows of
/// already-formatted cell values, it writes a fresh <c>{stem}-{timestamp}.xlsx</c> deliverable. One
/// writer instead of four near-identical ones (each feature only maps its typed rows to cells).
///
/// Cross-platform (ClosedXML, MIT) so worklists build/test off the box. Numeric cells are written as
/// numbers (decimal) so Excel sorts/sums them; everything else as text. Read-only deliverable — never
/// touches the PMS.
/// </summary>
public sealed class WorklistReportWriter
{
    private readonly ILogger<WorklistReportWriter> _logger;

    public WorklistReportWriter(ILogger<WorklistReportWriter> logger) => _logger = logger;

    /// <param name="cellsRows">Each inner array is one row, aligned to <paramref name="headers"/>. A cell
    /// is either a <see cref="decimal"/> (written numeric, 4-dp) / <see cref="int"/> / <see cref="DateOnly"/>,
    /// or anything else (written as its string). Null cells are blank.</param>
    public WorklistWriteResult Write(string outputDir, string stem, string sheetName,
        IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<object?>> cellsRows, string timestamp)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, $"{stem}-{timestamp}.xlsx");

            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet(sheetName);

            for (var c = 0; c < headers.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
            }

            for (var r = 0; r < cellsRows.Count; r++)
            {
                var row = cellsRows[r];
                for (var c = 0; c < row.Count; c++)
                    SetCell(ws.Cell(r + 2, c + 1), row[c]);
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(outputPath);

            _logger.LogInformation("WorklistReportWriter: wrote {Rows} rows to {Stem}", cellsRows.Count, stem);
            return WorklistWriteResult.Ok(outputPath, cellsRows.Count, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError("WorklistReportWriter failed for {Stem} ({ErrorType})", stem, ex.GetType().Name);
            return WorklistWriteResult.Fail("Worklist write failed");
        }
    }

    private static void SetCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null: cell.Value = ""; break;
            case decimal d: cell.Value = d; cell.Style.NumberFormat.Format = "0.0000"; break;
            case int i: cell.Value = i; break;
            case DateOnly date: cell.Value = date.ToDateTime(TimeOnly.MinValue); cell.Style.NumberFormat.Format = "yyyy-mm-dd"; break;
            default: cell.Value = value.ToString() ?? ""; break;
        }
    }
}

/// <summary>Result of a worklist write (mirrors the pricing WorklistWriteResult so callers are uniform).</summary>
public sealed record WorklistWriteResult
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public int Rows { get; init; }
    public int Skipped { get; init; }
    public string? Error { get; init; }

    public static WorklistWriteResult Ok(string path, int rows, int skipped) =>
        new() { Success = true, OutputPath = path, Rows = rows, Skipped = skipped };

    public static WorklistWriteResult Fail(string error) => new() { Success = false, Error = error };
}
