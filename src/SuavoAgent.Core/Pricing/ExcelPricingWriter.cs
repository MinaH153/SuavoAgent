using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Writes Best Supplier, Best Cost Per Unit, and Status columns into an Excel file.
/// Output mode is Sibling by default — produces a collision-resistant <c>{stem}-priced-{identity}.xlsx</c> next to the source
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
    private readonly Func<string, string> _siblingPathFactory;
    private readonly Action? _publicationPreparedObserver;

    public const string DefaultStatusHeader = "Price Lookup Status";

    public ExcelPricingWriter(
        ILogger<ExcelPricingWriter> logger,
        Func<string, string>? siblingPathFactory = null)
        : this(logger, siblingPathFactory, publicationPreparedObserver: null)
    {
    }

    internal ExcelPricingWriter(
        ILogger<ExcelPricingWriter> logger,
        Func<string, string>? siblingPathFactory,
        Action? publicationPreparedObserver)
    {
        _logger = logger;
        _siblingPathFactory = siblingPathFactory ?? ComputeSiblingPath;
        _publicationPreparedObserver = publicationPreparedObserver;
    }

    public WriteResult Write(
        string sourcePath,
        IReadOnlyList<SupplierPriceResult> results,
        string supplierColumnHeader = PricingJobDefaults.SupplierColumn,
        string costColumnHeader = PricingJobDefaults.CostColumn,
        string statusColumnHeader = DefaultStatusHeader,
        WriteMode mode = WriteMode.Sibling,
        int headerRow = 1,
        string? siblingPathAnchor = null) =>
        WriteCore(
            sourcePath,
            results,
            publicationGate: null,
            supplierColumnHeader,
            costColumnHeader,
            statusColumnHeader,
            mode,
            headerRow,
            siblingPathAnchor);

    internal WriteResult WriteAuthorized(
        string sourcePath,
        IReadOnlyList<SupplierPriceResult> results,
        Func<Action, PricingPublicationDecision> publicationGate,
        string supplierColumnHeader = PricingJobDefaults.SupplierColumn,
        string costColumnHeader = PricingJobDefaults.CostColumn,
        string statusColumnHeader = DefaultStatusHeader,
        WriteMode mode = WriteMode.Sibling,
        int headerRow = 1,
        string? siblingPathAnchor = null)
    {
        ArgumentNullException.ThrowIfNull(publicationGate);
        return WriteCore(
            sourcePath,
            results,
            publicationGate,
            supplierColumnHeader,
            costColumnHeader,
            statusColumnHeader,
            mode,
            headerRow,
            siblingPathAnchor);
    }

    private WriteResult WriteCore(
        string sourcePath,
        IReadOnlyList<SupplierPriceResult> results,
        Func<Action, PricingPublicationDecision>? publicationGate,
        string supplierColumnHeader,
        string costColumnHeader,
        string statusColumnHeader,
        WriteMode mode,
        int headerRow,
        string? siblingPathAnchor)
    {
        if (!File.Exists(sourcePath))
        {
            _logger.LogError("ExcelPricingWriter: source not found");
            return WriteResult.Fail("Source not found");
        }

        var outputPath = mode == WriteMode.InPlace
            ? sourcePath
            : _siblingPathFactory(siblingPathAnchor ?? sourcePath);

        if (mode == WriteMode.Sibling &&
            string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("core.excel_pricing_writer.output_identity_invalid");
            return WriteResult.Fail("pricing_output_identity_invalid");
        }

        if (mode == WriteMode.InPlace && IsFileLocked(sourcePath))
        {
            _logger.LogError(
                "ExcelPricingWriter: source workbook is locked (Excel open?) — aborting in-place write. " +
                "Re-run with WriteMode.Sibling or close the workbook.");
            return WriteResult.Fail("Source workbook is locked — close Excel and retry, or use Sibling mode.");
        }

        try
        {
            costColumnHeader = ExplicitPerUnitCostHeader(costColumnHeader);
            // Load the source workbook — for Sibling mode we'll save to a new path so the lock only
            // matters for Read access, which Excel.exe permits even with a file open.
            using var wb = new XLWorkbook(sourcePath);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws == null)
            {
                _logger.LogError("ExcelPricingWriter: no worksheet in source workbook");
                return WriteResult.Fail("No worksheet in source");
            }

            var supplierCol = FindOrCreateColumn(ws, supplierColumnHeader, headerRow);
            var costCol = FindOrCreateColumn(ws, costColumnHeader, headerRow);
            var statusCol = FindOrCreateColumn(ws, statusColumnHeader, headerRow);

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

            // Save to a same-directory CreateNew file, flush it, then publish in one filesystem
            // operation. Sibling publication uses File.Move(overwrite:false): concurrent jobs that
            // somehow choose the same identity cannot overwrite each other; exactly one wins.
            var tmp = CreatePublicationTempPath(outputPath);
            PricingPublicationDecision? deniedPublication = null;
            var cleanupFailed = false;
            try
            {
                using (var stream = new FileStream(
                           tmp,
                           FileMode.CreateNew,
                           FileAccess.ReadWrite,
                           FileShare.None,
                           bufferSize: 64 * 1024,
                           FileOptions.WriteThrough))
                {
                    wb.SaveAs(stream);
                    stream.Flush(flushToDisk: true);
                }

                _publicationPreparedObserver?.Invoke();

                void Publish()
                {
                    if (mode == WriteMode.Sibling)
                        File.Move(tmp, outputPath, overwrite: false);
                    else
                        File.Replace(tmp, outputPath, destinationBackupFileName: null);
                }

                if (publicationGate is null)
                {
                    Publish();
                }
                else
                {
                    var decision = publicationGate(Publish);
                    if (!decision.Published)
                        deniedPublication = decision;
                }
            }
            finally
            {
                if (File.Exists(tmp))
                {
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch (Exception ex)
                    {
                        cleanupFailed = true;
                        _logger.LogCritical(
                            "core.excel_pricing_writer.temp_cleanup_failed exception_type={ExceptionType}",
                            ex.GetType().Name);
                    }
                }
            }

            if (cleanupFailed)
                return WriteResult.Fail("pricing_publication_temp_cleanup_failed");
            if (deniedPublication is { } denied)
            {
                _logger.LogWarning(
                    "core.excel_pricing_writer.publication_denied code={Code}",
                    denied.Code);
                return WriteResult.PublicationDenied(denied.Code);
            }

            _logger.LogInformation(
                "ExcelPricingWriter: wrote {Ok} OK / {Fail} fail rows (mode={Mode})",
                okCount, failCount, mode);

            return WriteResult.Ok(outputPath, okCount, failCount);
        }
        catch (IOException) when (mode == WriteMode.Sibling && File.Exists(outputPath))
        {
            _logger.LogError("core.excel_pricing_writer.output_collision");
            return WriteResult.Fail("pricing_output_collision");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "core.excel_pricing_writer.failed exception_type={ExceptionType}",
                ex.GetType().Name);
            return WriteResult.Fail("Excel write failed");
        }
    }

    private static string ComputeSiblingPath(string source)
    {
        var dir = Path.GetDirectoryName(source) ?? Directory.GetCurrentDirectory();
        var stem = Path.GetFileNameWithoutExtension(source);
        var ext = Path.GetExtension(source);
        var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        return Path.Combine(dir, $"{stem}-priced-{ts}-{Guid.NewGuid():N}{ext}");
    }

    private static string CreatePublicationTempPath(string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory();
        var ext = Path.GetExtension(outputPath);
        return Path.Combine(dir, $".suavo-priced-{Guid.NewGuid():N}{ext}");
    }

    private static string ExplicitPerUnitCostHeader(string requested)
    {
        var normalized = requested.Trim();
        return normalized.Equals(
                   PricingJobDefaults.AmbiguousLegacyCostColumn,
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Cost", StringComparison.OrdinalIgnoreCase)
            ? PricingJobDefaults.CostColumn
            : requested;
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

    private static int FindOrCreateColumn(IXLWorksheet ws, string header, int headerRow)
    {
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        for (int c = 1; c <= lastCol; c++)
        {
            if (string.Equals(ws.Cell(headerRow, c).GetString()?.Trim(), header, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        var newCol = lastCol + 1;
        ws.Cell(headerRow, newCol).Value = header;
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
    /// <summary>Atomically publish a unique sibling file. Default — safe with Excel open.</summary>
    Sibling,
    /// <summary>Overwrite the source file. Refuses if the file is locked.</summary>
    InPlace,
}

internal readonly record struct PricingPublicationDecision(
    bool Published,
    string Code);

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
    public bool PublicationWasDenied { get; init; }
    public string? OutputPath { get; init; }
    public int OkRows { get; init; }
    public int FailRows { get; init; }
    public string? Error { get; init; }

    public static WriteResult Ok(string path, int ok, int fail) =>
        new() { Success = true, OutputPath = path, OkRows = ok, FailRows = fail };

    public static WriteResult Fail(string error) =>
        new() { Success = false, Error = error };

    internal static WriteResult PublicationDenied(string code) =>
        new()
        {
            Success = false,
            PublicationWasDenied = true,
            Error = code,
        };
}
