using System.IO.Compression;
using System.Xml.Linq;
using ClosedXML.Excel;

namespace SuavoAgent.Core.Tests.Pricing;

internal static class GooglePricingWorkbookFixture
{
    internal const string MetadataBase64 =
        "CgMxLjAaFAoBMRIKMjExODIzNjk1MCDoBygaSm8KBWVuX1VTEhNBbWVyaWNhL0xvc19BbmdlbGVzQBRIZFJNCgQIABgBEgROT05FGgZCT1RUT00iCE9WRVJGTE9XKgVBcmlhbDAKOABAAEgAUABYAGIICAAQAxgAIANqBwgCEP///wd6BAgCEACYAQA=";

    private const string EmptyGoogleDrawing =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r" +
        "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" xmlns:cx=\"http://schemas.microsoft.com/office/drawing/2014/chartex\" xmlns:cx1=\"http://schemas.microsoft.com/office/drawing/2015/9/8/chartex\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" xmlns:dgm=\"http://schemas.openxmlformats.org/drawingml/2006/diagram\" xmlns:x3Unk=\"http://schemas.microsoft.com/office/drawing/2010/slicer\" xmlns:sle15=\"http://schemas.microsoft.com/office/drawing/2012/slicer\"/>";

    internal static string Create(
        string directory,
        bool withFormula = false,
        bool withDrawingPayload = false,
        bool withUnknownMetadata = false,
        bool withPopulatedBlankNdcRow = false)
    {
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Sheet1");
            sheet.Cell(1, 1).Value = "Top 500 Most Dispensed Rx Items";
            sheet.Cell(6, 17).Value = "Acquisition\nCost";
            sheet.Cell(8, 1).Value = "#";
            sheet.Cell(8, 3).Value = "Drug";
            sheet.Cell(8, 4).Value = "Strength";
            sheet.Cell(8, 6).Value = "NDC";
            sheet.Cell(8, 7).Value = "Total Dispensed";
            sheet.Cell(8, 19).Value = "Price";
            WriteDataRow(sheet, 9, 1, "Drug A", "10 mg", "60505082901", 100m, 40m);
            // A page-break copy of the report header must not become an invalid NDC.
            sheet.Cell(10, 1).Value = "#";
            sheet.Cell(10, 3).Value = "Drug";
            sheet.Cell(10, 4).Value = "Strength";
            sheet.Cell(10, 6).Value = "NDC";
            sheet.Cell(10, 7).Value = "Total Dispensed";
            WriteDataRow(sheet, 11, 2, "Drug B", "20 mg", "59651000205", 80m, 30m);
            if (withPopulatedBlankNdcRow)
            {
                sheet.Cell(12, 1).Value = 3;
                sheet.Cell(12, 3).Value = "Drug C";
                sheet.Cell(12, 4).Value = "5 mg";
            }
            if (withFormula)
                sheet.Cell(11, 19).FormulaA1 = "=1+1";
            workbook.SaveAs(path);
        }

        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        Remove(archive, "docProps/core.xml");
        Remove(archive, "docProps/app.xml");
        Remove(archive, "xl/calcChain.xml");
        foreach (var entry in archive.Entries
                     .Where(entry => entry.FullName.StartsWith(
                         "package/services/metadata/core-properties/",
                         StringComparison.OrdinalIgnoreCase))
                     .ToArray())
            entry.Delete();

        UpdateXml(archive, "_rels/.rels", document =>
        {
            document.Root!.Elements().Where(element =>
                    element.Attribute("Type")?.Value.EndsWith("/officeDocument", StringComparison.Ordinal) != true)
                .Remove();
            var officeDocument = document.Root.Elements().Single();
            officeDocument.SetAttributeValue("Target", "xl/workbook.xml");
        });
        UpdateXml(archive, "[Content_Types].xml", document =>
        {
            document.Root!.Elements().Where(element =>
                    element.Attribute("PartName")?.Value.StartsWith("/docProps/", StringComparison.OrdinalIgnoreCase) == true ||
                    string.Equals(element.Attribute("PartName")?.Value, "/xl/calcChain.xml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(element.Attribute("Extension")?.Value, "psmdcp", StringComparison.OrdinalIgnoreCase))
                .Remove();
            var ns = document.Root.Name.Namespace;
            document.Root.Add(
                new XElement(ns + "Override",
                    new XAttribute("PartName", "/xl/metadata"),
                    new XAttribute("ContentType", "application/binary")),
                new XElement(ns + "Override",
                    new XAttribute("PartName", "/xl/drawings/drawing1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawing+xml")));
        });
        UpdateXml(archive, "xl/_rels/workbook.xml.rels", document =>
        {
            var ns = document.Root!.Name.Namespace;
            foreach (var relationship in document.Root.Elements())
            {
                var target = relationship.Attribute("Target")?.Value;
                if (target?.StartsWith("/xl/", StringComparison.Ordinal) == true)
                    relationship.SetAttributeValue("Target", target[4..]);
            }
            document.Root.Elements().Where(element =>
                    element.Attribute("Type")?.Value.EndsWith("/calcChain", StringComparison.Ordinal) == true)
                .Remove();
            document.Root.Add(new XElement(ns + "Relationship",
                new XAttribute("Id", "rIdGoogleMetadata"),
                new XAttribute("Type", "http://customschemas.google.com/relationships/workbookmetadata"),
                new XAttribute("Target", "metadata")));
        });
        UpdateXml(archive, "xl/workbook.xml", document =>
        {
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace go = "http://customooxmlschemas.google.com/";
            document.Root!.Add(new XElement(main + "extLst",
                new XElement(main + "ext",
                    new XAttribute("uri", "GoogleSheetsCustomDataVersion2"),
                    new XElement(go + "sheetsCustomData",
                        new XAttribute(rel + "id", "rIdGoogleMetadata"),
                        new XAttribute("roundtripDataChecksum", "fixture")))));
        });
        UpdateXml(archive, "xl/worksheets/sheet1.xml", document =>
        {
            XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            document.Root!.Add(new XElement(main + "drawing",
                new XAttribute(rel + "id", "rIdGoogleDrawing")));
        });

        var sheetRelationships = archive.CreateEntry("xl/worksheets/_rels/sheet1.xml.rels");
        using (var writer = new StreamWriter(sheetRelationships.Open()))
        {
            writer.Write("""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rIdGoogleDrawing" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing" Target="../drawings/drawing1.xml"/>
                </Relationships>
                """);
        }

        var drawing = archive.CreateEntry("xl/drawings/drawing1.xml");
        using (var writer = new StreamWriter(drawing.Open()))
            writer.Write(withDrawingPayload
                ? EmptyGoogleDrawing.Replace("/>", "><xdr:twoCellAnchor/></xdr:wsDr>", StringComparison.Ordinal)
                : EmptyGoogleDrawing);

        var metadata = archive.CreateEntry("xl/metadata");
        using (var stream = metadata.Open())
        {
            var bytes = withUnknownMetadata
                ? new byte[] { 0x01, 0x02, 0x03, 0x04 }
                : Convert.FromBase64String(MetadataBase64.Replace("\n", "", StringComparison.Ordinal));
            stream.Write(bytes);
        }

        return path;
    }

    private static void WriteDataRow(
        IXLWorksheet sheet,
        int row,
        int rank,
        string drug,
        string strength,
        string ndc,
        decimal totalDispensed,
        decimal acquisitionCost)
    {
        sheet.Cell(row, 1).Value = rank;
        sheet.Cell(row, 3).Value = drug;
        sheet.Cell(row, 4).Value = strength;
        sheet.Cell(row, 6).SetValue(ndc).Style.NumberFormat.Format = "@";
        sheet.Cell(row, 7).Value = totalDispensed;
        sheet.Cell(row, 17).Value = acquisitionCost;
    }

    private static void Remove(ZipArchive archive, string name) => archive.GetEntry(name)?.Delete();

    private static void UpdateXml(
        ZipArchive archive,
        string name,
        Action<XDocument> update)
    {
        var entry = archive.GetEntry(name) ?? throw new InvalidOperationException(name);
        XDocument document;
        using (var stream = entry.Open())
            document = XDocument.Load(stream, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        update(document);
        entry.Delete();
        var replacement = archive.CreateEntry(name);
        using var output = replacement.Open();
        document.Save(output, System.Xml.Linq.SaveOptions.DisableFormatting);
    }
}
