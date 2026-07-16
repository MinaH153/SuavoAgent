using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

public sealed partial class ExcelPricingWriter
{
    private static readonly string[] PackageOutputHeaders =
    {
        "Rank", "Drug", "Strength", "NDC", "Cheapest Supplier", "Cost",
    };

    private WriteResult WritePackageCostWorkbook(
        string sourcePath,
        string outputPath,
        IReadOnlyList<SupplierPriceResult> results,
        Func<Action, PricingPublicationDecision>? publicationGate,
        WriteMode mode,
        int headerRow)
    {
        try
        {
            using var output = new XLWorkbook();
            var target = output.AddWorksheet("Pricing");
            using (var source = new XLWorkbook(sourcePath))
            {
                var sourceSheet = source.Worksheets.FirstOrDefault();
                if (sourceSheet is null)
                    return WriteResult.Fail("No worksheet in source");
                var columns = ResolveExactSourceColumns(sourceSheet, headerRow);
                if (columns is null)
                    return WriteResult.Fail("pricing_package_source_schema_invalid");

                for (var index = 0; index < PackageOutputHeaders.Length; index++)
                    target.Cell(1, index + 1).Value = PackageOutputHeaders[index];

                var ordered = results.OrderBy(result => result.RowIndex).ToArray();
                for (var index = 0; index < ordered.Length; index++)
                {
                    var result = ordered[index];
                    if (result.CostBasis != PricingApprovalContract.PackageCostBasis ||
                        result.RowIndex <= headerRow)
                        return WriteResult.Fail("pricing_package_result_invalid");
                    var row = index + 2;
                    var drug = sourceSheet
                        .Cell(result.RowIndex, columns["Drug"]).GetString().Trim();
                    var strength = sourceSheet
                        .Cell(result.RowIndex, columns["Strength"]).GetString().Trim();
                    if (drug.Length == 0 || strength.Length == 0)
                        return WriteResult.Fail("pricing_package_source_row_invalid");
                    target.Cell(row, 1).Value = index + 1;
                    target.Cell(row, 2).Value = drug;
                    target.Cell(row, 3).Value = strength;
                    var ndc = PricingResultContentPolicy.CanonicalNdcOrNull(result.Ndc) ?? "";
                    target.Cell(row, 4).SetValue(ndc);
                    target.Cell(row, 4).Style.NumberFormat.Format = "@";
                    if (result.Found && result.PackageCost is { } packageCost)
                    {
                        target.Cell(row, 5).Value = result.SupplierName;
                        target.Cell(row, 6).Value = packageCost;
                        target.Cell(row, 6).Style.NumberFormat.Format = "$0.0000";
                    }
                    else
                    {
                        target.Cell(row, 5).Value = "Needs review";
                        target.Cell(row, 6).Clear(XLClearOptions.Contents);
                        target.Range(row, 1, row, PackageOutputHeaders.Length)
                            .Style.Fill.BackgroundColor = XLColor.FromHtml("#FFF7ED");
                        target.Cell(row, 5).Style.Font.FontColor =
                            XLColor.FromHtml("#92400E");
                        target.Cell(row, 5).Style.Font.Bold = true;
                    }
                }
            }

            var header = target.Range(1, 1, 1, PackageOutputHeaders.Length);
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#1D4ED8");
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Font.Bold = true;
            header.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            target.Row(1).Height = 24;
            target.SheetView.FreezeRows(1);
            target.Range(
                    1,
                    1,
                    Math.Max(1, results.Count + 1),
                    PackageOutputHeaders.Length)
                .SetAutoFilter();
            target.Column(1).Width = 8;
            target.Column(2).Width = 34;
            target.Column(3).Width = 14;
            target.Column(4).Width = 16;
            target.Column(5).Width = 26;
            target.Column(6).Width = 14;
            var tempPath = CreatePublicationTempPath(outputPath);
            PricingPublicationDecision? denied = null;
            var published = false;
            var cleanupFailed = false;
            try
            {
                using (var stream = new FileStream(
                           tempPath,
                           FileMode.CreateNew,
                           FileAccess.ReadWrite,
                           FileShare.None,
                           64 * 1024,
                           FileOptions.WriteThrough))
                {
                    output.SaveAs(stream);
                    stream.Flush(flushToDisk: true);
                }
                _publicationPreparedObserver?.Invoke();
                void Publish()
                {
                    if (mode == WriteMode.Sibling)
                        CreateNoClobberHardLink(outputPath, tempPath);
                    else
                        File.Replace(tempPath, outputPath, null);
                    published = true;
                }
                if (publicationGate is null)
                    Publish();
                else
                {
                    var decision = publicationGate(Publish);
                    if (!decision.Published) denied = decision;
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); }
                    catch (Exception exception)
                    {
                        cleanupFailed = !published;
                        _logger.LogCritical(
                            "core.excel_pricing_writer.package_temp_cleanup_failed exception_type={ExceptionType}",
                            exception.GetType().Name);
                    }
                }
            }
            if (cleanupFailed)
                return WriteResult.Fail("pricing_publication_temp_cleanup_failed");
            if (denied is { } rejection)
                return WriteResult.PublicationDenied(rejection.Code);
            return WriteResult.Ok(
                outputPath,
                results.Count(result => result.Found),
                results.Count(result => !result.Found));
        }
        catch (IOException) when (mode == WriteMode.Sibling && File.Exists(outputPath))
        {
            return WriteResult.Fail("pricing_output_collision");
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "core.excel_pricing_writer.package_failed exception_type={ExceptionType}",
                exception.GetType().Name);
            return WriteResult.Fail("Excel write failed");
        }
    }

    private static Dictionary<string, int>? ResolveExactSourceColumns(
        IXLWorksheet worksheet,
        int headerRow)
    {
        var required = new[] { "Drug", "Strength", "NDC" };
        var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastColumn = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        foreach (var name in required)
        {
            var matches = Enumerable.Range(1, lastColumn)
                .Where(column => string.Equals(
                    worksheet.Cell(headerRow, column).GetString().Trim(),
                    name,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1) return null;
            resolved[name] = matches[0];
        }
        return resolved;
    }
}
