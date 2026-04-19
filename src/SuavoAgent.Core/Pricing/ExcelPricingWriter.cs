using OfficeOpenXml;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Writes Supplier and Cost columns into an existing Excel file.
/// Creates the columns if they don't exist; otherwise overwrites values.
/// Saves atomically (temp → copy).
/// </summary>
public sealed class ExcelPricingWriter
{
    private readonly ILogger<ExcelPricingWriter> _logger;

    public ExcelPricingWriter(ILogger<ExcelPricingWriter> logger)
    {
        _logger = logger;
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
    }

    public bool Write(string path, IReadOnlyList<SupplierPriceResult> results,
        string supplierColumnHeader = "Supplier", string costColumnHeader = "Cost (per unit)")
    {
        if (!File.Exists(path))
        {
            _logger.LogError("ExcelPricingWriter: file not found {Path}", path);
            return false;
        }

        try
        {
            using var package = new ExcelPackage(new FileInfo(path));
            var ws = package.Workbook.Worksheets.FirstOrDefault();
            if (ws == null || ws.Dimension == null)
            {
                _logger.LogError("ExcelPricingWriter: no worksheet in {Path}", path);
                return false;
            }

            var supplierCol = FindOrCreateColumn(ws, supplierColumnHeader);
            var costCol = FindOrCreateColumn(ws, costColumnHeader);

            foreach (var r in results)
            {
                if (r.RowIndex < 2) continue;

                if (r.Found)
                {
                    ws.Cells[r.RowIndex, supplierCol].Value = r.SupplierName ?? "";
                    ws.Cells[r.RowIndex, costCol].Value = r.CostPerUnit.HasValue
                        ? (object)r.CostPerUnit.Value
                        : "";
                }
                else
                {
                    // [M-5] Clear stale supplier/cost values on failed rows so they
                    // don't appear valid from a prior successful run.
                    ws.Cells[r.RowIndex, supplierCol].Value = "";
                    ws.Cells[r.RowIndex, costCol].Value = "";
                }
            }

            // Atomic save: write to temp then replace
            var tmp = path + ".tmp";
            package.SaveAs(new FileInfo(tmp));
            File.Move(tmp, path, overwrite: true);

            _logger.LogInformation("ExcelPricingWriter: wrote {Count} results to {Path}",
                results.Count(r => r.Found), path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExcelPricingWriter failed for {Path}", path);
            return false;
        }
    }

    private static int FindOrCreateColumn(ExcelWorksheet ws, string header)
    {
        int lastCol = ws.Dimension?.End.Column ?? 0;
        for (int c = 1; c <= lastCol; c++)
        {
            if (string.Equals(ws.Cells[1, c].Text?.Trim(), header, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        // Append new column
        var newCol = lastCol + 1;
        ws.Cells[1, newCol].Value = header;
        return newCol;
    }
}
