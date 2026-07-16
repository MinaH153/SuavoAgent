using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Validates the printed PioneerRx workbook itself, independently of the UI
/// state and export receipt. This rejects stale or page-truncated reports that
/// happen to contain some readable NDC rows.
/// </summary>
public static partial class PioneerRxTop500ExportWorkbookValidator
{
    private const int ExpectedPages = 18;

    private static readonly string ExpectedStatusLine =
        "Transaction Status: " +
        string.Join(", ", PioneerRxTop500ReportRecipe.IncludedStatuses);

    private static readonly string[] ReportTimestampFormats =
    [
        "M/d/yyyy h:mm tt",
        "M/d/yyyy hh:mm tt",
        "M/d/yyyy h:mm:ss tt",
        "M/d/yyyy hh:mm:ss tt",
        "M/d/yyyy",
        "MM/dd/yyyy",
    ];

    public static bool IsExact(string path, DateOnly expectedRunDate)
    {
        try
        {
            using var workbook = new XLWorkbook(path);
            return IsExact(workbook, expectedRunDate);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsExact(byte[] bytes, DateOnly expectedRunDate)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var workbook = new XLWorkbook(stream);
            return IsExact(workbook, expectedRunDate);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsExact(XLWorkbook workbook, DateOnly expectedRunDate)
    {
        if (workbook.Worksheets.Count != 1) return false;
        var worksheet = workbook.Worksheets.Single();
        if (!string.Equals(
                worksheet.Cell("A1").GetString().Trim(),
                "Top 500 Most Dispensed Rx Items",
                StringComparison.Ordinal))
            return false;
        if (!PreambleIsExact(worksheet.Cell("A5").GetString(), expectedRunDate))
            return false;
        if (worksheet.LastRowUsed()?.RowNumber() != 662 ||
            worksheet.LastColumnUsed()?.ColumnNumber() != 19)
            return false;

        var ranks = new List<int>(PioneerRxTop500ReportRecipe.TopCount);
        var ndcs = new HashSet<string>(StringComparer.Ordinal);
        var headerCount = 0;
        var footers = new List<(int Current, int Total)>();
        var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
        for (var row = 1; row <= lastRow; row++)
        {
            var first = worksheet.Cell(row, 1).GetString().Trim();
            if (string.Equals(first, "#", StringComparison.Ordinal))
            {
                if (!HeaderIsExact(worksheet, row)) return false;
                headerCount++;
                continue;
            }
            if (int.TryParse(
                    first,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var rank) &&
                rank is >= 1 and <= PioneerRxTop500ReportRecipe.TopCount)
            {
                var ndcCell = worksheet.Cell(row, 6);
                var ndc = ndcCell.GetString().Trim();
                if (ndcCell.DataType != XLDataType.Text ||
                    ndc.Length != 11 ||
                    ndc.Any(character => character is < '0' or > '9') ||
                    !ndcs.Add(ndc))
                    return false;
                ranks.Add(rank);
                continue;
            }
            if (row <= 8) continue;
            if (IsExactPageHeaderFurnitureCopy(worksheet, row)) continue;
            if (TryReadExactPageFooter(
                    worksheet,
                    row,
                    expectedRunDate,
                    out var footer))
            {
                footers.Add(footer);
                continue;
            }
            if (worksheet.Row(row).CellsUsed().Any()) return false;
        }

        return headerCount == ExpectedPages &&
               ranks.SequenceEqual(Enumerable.Range(
                   1,
                   PioneerRxTop500ReportRecipe.TopCount)) &&
               ndcs.Count == PioneerRxTop500ReportRecipe.TopCount &&
               footers.Count == ExpectedPages &&
               footers.Select(footer => footer.Current)
                   .SequenceEqual(Enumerable.Range(1, ExpectedPages)) &&
               footers.All(footer => footer.Total == ExpectedPages);
    }

    private static bool IsExactPageHeaderFurnitureCopy(
        IXLWorksheet worksheet,
        int candidateRow)
    {
        for (var sourceRow = 1; sourceRow <= 7; sourceRow++)
        {
            var exact = true;
            for (var column = 1; column <= 19; column++)
            {
                if (string.Equals(
                        worksheet.Cell(sourceRow, column).GetString().Trim(),
                        worksheet.Cell(candidateRow, column).GetString().Trim(),
                        StringComparison.Ordinal))
                    continue;
                exact = false;
                break;
            }
            if (exact) return true;
        }
        return false;
    }

    private static bool TryReadExactPageFooter(
        IXLWorksheet worksheet,
        int row,
        DateOnly expectedRunDate,
        out (int Current, int Total) footer)
    {
        footer = default;
        var cells = worksheet.Row(row).CellsUsed().ToArray();
        if (cells.Length != 2) return false;
        var pageCell = cells.SingleOrDefault(cell =>
            PageFooterPattern().IsMatch(cell.GetString().Trim()));
        if (pageCell is null) return false;
        var dateCell = cells.Single(cell => !ReferenceEquals(cell, pageCell));
        if (!DateTime.TryParse(
                dateCell.GetString().Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var printedDate) ||
            DateOnly.FromDateTime(printedDate) != expectedRunDate)
            return false;
        var match = PageFooterPattern().Match(pageCell.GetString().Trim());
        footer = (
            int.Parse(match.Groups["current"].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups["total"].Value, CultureInfo.InvariantCulture));
        return true;
    }

    private static bool HeaderIsExact(IXLWorksheet worksheet, int row) =>
        string.Equals(worksheet.Cell(row, 3).GetString().Trim(), "Drug",
            StringComparison.Ordinal) &&
        string.Equals(worksheet.Cell(row, 4).GetString().Trim(), "Strength",
            StringComparison.Ordinal) &&
        string.Equals(worksheet.Cell(row, 6).GetString().Trim(), "NDC",
            StringComparison.Ordinal) &&
        string.Equals(worksheet.Cell(row, 7).GetString().Trim(), "Total Dispensed",
            StringComparison.Ordinal) &&
        string.Equals(worksheet.Cell(row, 19).GetString().Trim(), "Price",
            StringComparison.Ordinal);

    private static bool PreambleIsExact(string value, DateOnly expectedRunDate)
    {
        var lines = value.Replace("\r", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 5 ||
            !string.Equals(lines[0],
                "Dispensed Item Brand/Generic: Generic", StringComparison.Ordinal) ||
            !string.Equals(lines[1],
                "Dispensed Item Dea Schedule: No Schedule", StringComparison.Ordinal) ||
            !string.Equals(lines[3],
                "Rx Transaction: Removed From Inventory", StringComparison.Ordinal) ||
            !string.Equals(lines[4], ExpectedStatusLine, StringComparison.Ordinal))
            return false;

        var dateMatch = CompletedBetweenPattern().Match(lines[2]);
        if (!dateMatch.Success ||
            !TryParseReportDate(dateMatch.Groups["from"].Value, out var from) ||
            !TryParseReportDate(dateMatch.Groups["through"].Value, out var through))
            return false;
        return through == expectedRunDate &&
               from == PioneerRxTop500ReportRecipe.StartFor(expectedRunDate);
    }

    private static bool TryParseReportDate(string value, out DateOnly date)
    {
        date = default;
        if (!DateTime.TryParseExact(
                value.Trim(),
                ReportTimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
            return false;
        date = DateOnly.FromDateTime(parsed);
        return true;
    }

    [GeneratedRegex(
        @"^Completed On Between:\s*(?<from>.+?)\s+and\s+(?<through>.+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompletedBetweenPattern();

    [GeneratedRegex(
        @"^Page\s+(?<current>[1-9]\d*)\s+of\s+(?<total>[1-9]\d*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PageFooterPattern();
}
