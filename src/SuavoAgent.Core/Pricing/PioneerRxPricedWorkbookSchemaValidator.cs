using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Exact local schema gate shared by Core before upload and Helper before
/// atomic publication. It admits one visible, formula-free A1:F501 worksheet
/// and no other workbook content.
/// </summary>
public static class PioneerRxPricedWorkbookSchemaValidator
{
    public static bool IsExact(string path)
    {
        try
        {
            using var workbook = new XLWorkbook(path);
            if (workbook.Worksheets.Count != 1) return false;
            var worksheet = workbook.Worksheets.Single();
            if (worksheet.Visibility != XLWorksheetVisibility.Visible ||
                !string.Equals(worksheet.Name, "Pricing", StringComparison.Ordinal) ||
                worksheet.Row(1).IsHidden ||
                worksheet.MergedRanges.Any())
                return false;

            var used = worksheet.RangeUsed();
            if (used is null ||
                used.RangeAddress.FirstAddress.RowNumber != 1 ||
                used.RangeAddress.FirstAddress.ColumnNumber != 1 ||
                used.RangeAddress.LastAddress.RowNumber !=
                    PioneerRxPricedWorkbookPublicationContract.ExpectedDataRows + 1 ||
                used.RangeAddress.LastAddress.ColumnNumber !=
                    PioneerRxPricedWorkbookPublicationContract.ExpectedHeaders.Count ||
                used.Cells().Any(cell => cell.HasFormula))
                return false;

            for (var column = 1;
                 column <= PioneerRxPricedWorkbookPublicationContract.ExpectedHeaders.Count;
                 column++)
            {
                if (worksheet.Column(column).IsHidden ||
                    !string.Equals(
                        worksheet.Cell(1, column).GetString(),
                        PioneerRxPricedWorkbookPublicationContract.ExpectedHeaders[column - 1],
                        StringComparison.Ordinal))
                    return false;
            }

            var ndcs = new HashSet<string>(StringComparer.Ordinal);
            for (var row = 2;
                 row <= PioneerRxPricedWorkbookPublicationContract.ExpectedDataRows + 1;
                 row++)
            {
                if (worksheet.Row(row).IsHidden) return false;
                var rankCell = worksheet.Cell(row, 1);
                if (rankCell.DataType != XLDataType.Number ||
                    !rankCell.TryGetValue<int>(out var rank) ||
                    rank != row - 1)
                    return false;
                if (string.IsNullOrWhiteSpace(worksheet.Cell(row, 2).GetString()) ||
                    string.IsNullOrWhiteSpace(worksheet.Cell(row, 3).GetString()))
                    return false;

                var ndcCell = worksheet.Cell(row, 4);
                var ndc = ndcCell.GetString();
                if (ndcCell.DataType != XLDataType.Text ||
                    ndc.Length != 11 ||
                    ndc.Any(character => character is < '0' or > '9') ||
                    !ndcs.Add(ndc))
                    return false;

                var supplier = worksheet.Cell(row, 5).GetString().Trim();
                var costCell = worksheet.Cell(row, 6);
                if (string.Equals(supplier, "Needs review", StringComparison.Ordinal))
                {
                    if (!costCell.IsEmpty()) return false;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(supplier) ||
                        costCell.DataType != XLDataType.Number ||
                        !costCell.TryGetValue<decimal>(out var cost) ||
                        cost <= 0m)
                        return false;
                }
            }

            return ndcs.Count ==
                PioneerRxPricedWorkbookPublicationContract.ExpectedDataRows;
        }
        catch
        {
            return false;
        }
    }
}
