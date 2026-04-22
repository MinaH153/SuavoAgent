using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Writes Supplier, Cost, and Status columns into an Excel file.
/// Output mode is Sibling by default — produces <c>{stem}-priced-{ts}.xlsx</c> next to the source
/// workbook. In-place mode is available for explicit re-run scenarios; it first verifies the source
/// is not locked by Excel.exe to avoid the "succeed 499 rows, fail at move" Codex scenario.
///
/// Three data columns are always written:
///   • Supplier header      (from <see cref="PricingJobSpec.SupplierColumn"/>)
///   • Cost header          (from <see cref="PricingJobSpec.CostColumn"/>)
///   • Status header        ("Price Lookup Status") — explicit markers per row
///
/// Status markers:
///   • OK                   — supplier + cost populated
///   • NO_MATCH             — NDC not found in PioneerRx
///   • NO_SUPPLIER_ROWS     — NDC found but no suppliers listed
///   • MULTIPLE_MATCHES     — ambiguous item match (flag, do not auto-pick)
///   • LOCKED_SOURCE        — Excel file held a lock; row skipped
///   • ERROR:{message}      — other lookup failures
/// </summary>
public sealed class ExcelPricingWriter
{
    private readonly ILogger<ExcelPricingWriter> _logger;

    public const string DefaultStatusHeader = "Price Lookup Status";

    public ExcelPricingWriter(ILogger<ExcelPricingWriter> logger)
    {
        _logger = logger;
    }

    public WriteResult Write(
        string sourcePath,
        IReadOnlyList<SupplierPriceResult> results,
        string supplierColumnHeader = "Supplier",
        string costColumnHeader = "Cost (per unit)",
        string statusColumnHeader = DefaultStatusHeader,
        WriteMode mode = WriteMode.Sibling)
    {
        if (!File.Exists(sourcePath))
        {
            _logger.LogError("ExcelPricingWriter: source not found {Path}", sourcePath);
            return WriteResult.Fail($"Source not found: {sourcePath}");
        }

        var outputPath = mode == WriteMode.InPlace
            ? sourcePath
            : ComputeSiblingPath(sourcePath);

        if (mode == WriteMode.InPlace && IsFileLocked(sourcePath))
        {
            _logger.LogError(
                "ExcelPricingWriter: source {Path} is locked (Excel open?) — aborting in-place write. " +
                "Re-run with WriteMode.Sibling or close the workbook.", sourcePath);
            return WriteResult.Fail("Source workbook is locked — close Excel and retry, or use Sibling mode.");
        }

        try
        {
            // Load the source workbook — for Sibling mode we'll save to a new path so the lock only
            // matters for Read access, which Excel.exe permits even with a file open.
            using var wb = new XLWorkbook(sourcePath);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null)
            {
                _logger.LogError("ExcelPricingWriter: no worksheet in {Path}", sourcePath);
                return WriteResult.Fail("No worksheet in source");
            }

            var supplierCol = FindOrCreateColumn(ws, supplierColumnHeader);
            var costCol = FindOrCreateColumn(ws, costColumnHeader);
            var statusCol = FindOrCreateColumn(ws, statusColumnHeader);

            int okCount = 0, failCount = 0;
            foreach (var r in results)
            {
                if (r.RowIndex < 2) continue;

                if (r.Found && !string.IsNullOrEmpty(r.SupplierName) && r.CostPerUnit.HasValue)
                {
                    ws.Cell(r.RowIndex, supplierCol).Value = r.SupplierName;
                    ws.Cell(r.RowIndex, costCol).Value = r.CostPerUnit.Value;
                    ws.Cell(r.RowIndex, statusCol).Value = StatusMarkers.Ok;
                    okCount++;
                }
                else
                {
                    // Blank the data cells but write an explicit marker so the operator can
                    // tell "no data yet" from "looked up, nothing found".
                    ws.Cell(r.RowIndex, supplierCol).Value = "";
                    ws.Cell(r.RowIndex, costCol).Value = "";
                    ws.Cell(r.RowIndex, statusCol).Value = MarkerFor(r);
                    failCount++;
                }
            }

            // ClosedXML enforces an Excel extension, so temp files need .xlsx, not .tmp.
            // Sibling mode: write directly — the path doesn't exist yet, no atomicity risk.
            // InPlace mode: write-then-replace with a sibling .xlsx tempfile to keep the
            // "never leave a half-written workbook in place" guarantee Codex asked for.
            if (mode == WriteMode.Sibling)
            {
                wb.SaveAs(outputPath);
            }
            else
            {
                var tmp = Path.Combine(
                    Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory(),
                    $".suavo-priced-{Guid.NewGuid():N}.xlsx");
                try
                {
                    wb.SaveAs(tmp);
                    File.Replace(tmp, outputPath, destinationBackupFileName: null);
                }
                finally
                {
                    if (File.Exists(tmp))
                    {
                        try { File.Delete(tmp); } catch { /* best effort cleanup */ }
                    }
                }
            }

            _logger.LogInformation(
                "ExcelPricingWriter: wrote {Ok} OK / {Fail} fail rows to {Path} (mode={Mode})",
                okCount, failCount, outputPath, mode);

            return WriteResult.Ok(outputPath, okCount, failCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ExcelPricingWriter failed for {Path}", sourcePath);
            return WriteResult.Fail(ex.Message);
        }
    }

    private static string ComputeSiblingPath(string source)
    {
        var dir = Path.GetDirectoryName(source) ?? Directory.GetCurrentDirectory();
        var stem = Path.GetFileNameWithoutExtension(source);
        var ext = Path.GetExtension(source);
        var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(dir, $"{stem}-priced-{ts}{ext}");
    }

    /// <summary>
    /// True if the file cannot be opened for writing (lock held — typically Excel.exe).
    /// Not authoritative for Sibling mode (we only need read access there), but
    /// required for InPlace mode to fail fast before processing rows.
    /// </summary>
    internal static bool IsFileLocked(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static int FindOrCreateColumn(IXLWorksheet ws, string header)
    {
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (int c = 1; c <= lastCol; c++)
        {
            if (string.Equals(ws.Cell(1, c).GetString()?.Trim(), header, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        var newCol = lastCol + 1;
        ws.Cell(1, newCol).Value = header;
        return newCol;
    }

    private static string MarkerFor(SupplierPriceResult r)
    {
        if (string.IsNullOrEmpty(r.ErrorMessage))
            return StatusMarkers.NoSupplierRows;

        var msg = r.ErrorMessage.ToLowerInvariant();
        if (msg.Contains("no supplier rows") || msg.Contains("no supplier"))
            return StatusMarkers.NoSupplierRows;
        if (msg.Contains("not match") || msg.Contains("no match"))
            return StatusMarkers.NoMatch;
        if (msg.Contains("multiple"))
            return StatusMarkers.MultipleMatches;

        return $"ERROR: {r.ErrorMessage}";
    }
}

public enum WriteMode
{
    /// <summary>Write a sibling file <c>{stem}-priced-{ts}.xlsx</c>. Default — safe with Excel open.</summary>
    Sibling,
    /// <summary>Overwrite the source file. Refuses if the file is locked.</summary>
    InPlace,
}

public static class StatusMarkers
{
    public const string Ok = "OK";
    public const string NoMatch = "NO_MATCH";
    public const string NoSupplierRows = "NO_SUPPLIER_ROWS";
    public const string MultipleMatches = "MULTIPLE_MATCHES";
    public const string LockedSource = "LOCKED_SOURCE";
}

public sealed record WriteResult
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public int OkRows { get; init; }
    public int FailRows { get; init; }
    public string? Error { get; init; }

    public static WriteResult Ok(string path, int ok, int fail) =>
        new() { Success = true, OutputPath = path, OkRows = ok, FailRows = fail };

    public static WriteResult Fail(string error) =>
        new() { Success = false, Error = error };
}
