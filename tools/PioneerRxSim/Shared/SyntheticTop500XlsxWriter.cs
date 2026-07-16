using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace PioneerRxSim;

public static class SyntheticTop500XlsxWriter
{
    public static string Write(string directory, DateTimeOffset now)
    {
        var stem = $"Top_500_Most_Dispensed_Rx_Items_{now:yyyyMMdd_HHmmssfff}";
        var final = Path.Combine(directory, $"{stem}.xlsx");
        return WriteToPath(final, now);
    }

    public static string WriteToPath(string path, DateTimeOffset now)
    {
        var final = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(final), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Synthetic Top-500 output must be XLSX.", nameof(path));
        var directory = Path.GetDirectoryName(final)
            ?? throw new ArgumentException("Synthetic Top-500 output directory is missing.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(final)}.{Guid.NewGuid():N}.partial");
        using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            Entry(archive, "[Content_Types].xml", ContentTypes);
            Entry(archive, "_rels/.rels", PackageRelationships);
            Entry(archive, "xl/workbook.xml", Workbook);
            Entry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships);
            Entry(
                archive,
                "xl/worksheets/sheet1.xml",
                BuildWorksheet(DateOnly.FromDateTime(now.DateTime)));
        }
        File.Move(temporary, final, overwrite: false);
        return final;
    }

    private static string BuildWorksheet(DateOnly runDate)
    {
        var rows = new StringBuilder(180_000);
        rows.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
            .Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        var worksheetRow = 1;
        var rank = 1;
        const int pages = 18;
        for (var page = 1; page <= pages; page++)
        {
            AppendPageHeader(rows, ref worksheetRow, runDate);
            var pageRows = page <= 14 ? 28 : 27;
            for (var index = 0; index < pageRows; index++, rank++)
            {
                var ndc = rank <= 40
                    ? (1_000_000_000L + rank).ToString("D10", CultureInfo.InvariantCulture)
                        .Insert(0, "0")
                    : (10_000_000_000L + rank).ToString(CultureInfo.InvariantCulture);
                rows.Append(CellsRow(
                    worksheetRow++,
                    ("A", rank.ToString(CultureInfo.InvariantCulture), false),
                    ("C", $"Synthetic Generic {rank:000}", false),
                    ("D", $"{(rank % 20) + 1} mg", false),
                    ("F", ndc, false),
                    ("G", (1000 - rank).ToString(CultureInfo.InvariantCulture), true),
                    ("S", (rank / 10m).ToString("0.00", CultureInfo.InvariantCulture), true)));
            }
            rows.Append(CellsRow(
                worksheetRow++,
                ("A", runDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture), false),
                ("S", $"Page {page} of {pages}", false)));
        }
        if (rank != 501) throw new InvalidOperationException("Synthetic Top-500 row count drifted.");
        return rows.Append("</sheetData></worksheet>").ToString();
    }

    private static void AppendPageHeader(
        StringBuilder rows,
        ref int row,
        DateOnly runDate)
    {
        var from = new DateOnly(runDate.Year, 1, 1);
        var preamble =
            "Dispensed Item Brand/Generic: Generic\n" +
            "Dispensed Item Dea Schedule: No Schedule\n" +
            $"Completed On Between: {from.ToString("M/d/yyyy", CultureInfo.InvariantCulture)} 12:00 AM and {runDate.ToString("M/d/yyyy", CultureInfo.InvariantCulture)} 12:00 AM\n" +
            "Rx Transaction: Removed From Inventory\n" +
            "Transaction Status: Completed, Out for Delivery, To Be Put in Bin, Waiting for Central Fill, Waiting for Check, Waiting for Delivery, Waiting for Fill, Waiting for Pick up";
        rows.Append(CellsRow(row++, ("A", "Top 500 Most Dispensed Rx Items", false)))
            .Append(CellsRow(row++, ("A", "PioneerRx Simulation Pharmacy", false)))
            .Append(CellsRow(row++, ("A", "Simulation Address", false)))
            .Append(CellsRow(row++, ("A", "Simulation City", false)))
            .Append(CellsRow(row++, ("A", preamble, false)))
            .Append(CellsRow(
                row++,
                ("H", "New Fill Total Dispensed", false),
                ("I", "Refill Total Dispensed", false)))
            .Append(CellsRow(
                row++,
                ("J", "Total Fills", false),
                ("K", "New Fills", false),
                ("L", "Refills", false),
                ("M", "Inventory Group", false),
                ("N", "Acquisition Cost", false)))
            .Append(CellsRow(
                row++,
                ("A", "#", false),
                ("C", "Drug", false),
                ("D", "Strength", false),
                ("F", "NDC", false),
                ("G", "Total Dispensed", false),
                ("S", "Price", false)));
    }

    private static string CellsRow(
        int row,
        params (string Column, string Value, bool Numeric)[] cells) =>
        $"<row r=\"{row}\">" +
        string.Concat(cells.Select(cell => cell.Numeric
            ? $"<c r=\"{cell.Column}{row}\"><v>{cell.Value}</v></c>"
            : InlineCell($"{cell.Column}{row}", cell.Value))) +
        "</row>";

    private static string InlineCell(string reference, string value) =>
        $"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value)}</t></is></c>";

    private static void Entry(ZipArchive archive, string path, string contents)
    {
        using var stream = archive.CreateEntry(path, CompressionLevel.Fastest).Open();
        var bytes = Encoding.UTF8.GetBytes(contents);
        stream.Write(bytes);
    }

    private const string ContentTypes =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
        "</Types>";

    private const string PackageRelationships =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
        "</Relationships>";

    private const string Workbook =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
        "<sheets><sheet name=\"Top 500\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

    private const string WorkbookRelationships =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
        "</Relationships>";
}
