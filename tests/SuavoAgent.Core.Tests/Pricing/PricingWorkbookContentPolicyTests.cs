using System.IO.Compression;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PricingWorkbookContentPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"suavo_ooxml_policy_{Guid.NewGuid():N}");

    public PricingWorkbookContentPolicyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Validate_AllowsNormalDrugPricingColumns()
    {
        var path = Workbook("NDC", "Drug Name", "RxBIN", "Price");

        var result = PricingWorkbookContentPolicy.Validate(path);

        Assert.Equal(new FileInfo(path).Length, result.SizeBytes);
        Assert.Matches("^[0-9a-f]{64}$", result.Sha256);
    }

    [Theory]
    [InlineData("Patient")]
    [InlineData("Patient Name")]
    [InlineData("CustomerName")]
    [InlineData("Member Full Name")]
    [InlineData("DOB")]
    [InlineData("Address")]
    [InlineData("Phone")]
    [InlineData("Rx Number")]
    [InlineData("Prescription #")]
    [InlineData("Diagnosis")]
    public void Validate_RejectsPhiLikeFieldsWithoutReturningCellText(string field)
    {
        var path = Workbook("NDC", field, "Price");

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_phi_field_forbidden", error.Code);
        Assert.DoesNotContain(field, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("MRN")]
    [InlineData("SSN")]
    [InlineData("Email")]
    [InlineData("First Name")]
    [InlineData("Last Name")]
    [InlineData("BirthDate")]
    public void Validate_RejectsEveryUnapprovedPricingColumn(string field)
    {
        var path = Workbook("NDC", "Drug Name", field, "Price");

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_pricing_schema_forbidden", error.Code);
        Assert.DoesNotContain(field, error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsUnexpectedWorksheetEvenWhenPricingSheetIsValid()
    {
        var path = Workbook("NDC", "Drug Name", "Price");
        using (var workbook = new XLWorkbook(path))
        {
            workbook.AddWorksheet("Other");
            workbook.Save();
        }

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_pricing_schema_forbidden", error.Code);
    }

    [Fact]
    public void Validate_AllowsExactPioneerRxTop500SchemaAfterPreamble()
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Top 500");
            sheet.Cell(1, 1).Value = "Top 500 Most Dispensed Rx Items";
            sheet.Cell(2, 1).Value = "Pharmacy export";
            var headers = new[]
            {
                "#", "Drug", "Strength", "NDC", "Total Dispensed", "Acquisition Cost",
            };
            for (var index = 0; index < headers.Length; index++)
                sheet.Cell(6, index + 1).Value = headers[index];
            sheet.Cell(7, 1).Value = 1;
            sheet.Cell(7, 2).Value = "Example";
            sheet.Cell(7, 3).Value = "10 mg";
            sheet.Cell(7, 4).Value = "00000000000";
            sheet.Cell(7, 5).Value = 100;
            sheet.Cell(7, 6).Value = 1.25;
            workbook.SaveAs(path);
        }

        var result = PricingWorkbookContentPolicy.Validate(path);

        Assert.True(result.SizeBytes > 0);
    }

    [Fact]
    public void Validate_RejectsEmbeddedActiveContent()
    {
        var path = Workbook("NDC", "Drug Name", "Price");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            archive.CreateEntry("xl/embeddings/object.bin");

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_active_content_forbidden", error.Code);
    }

    [Fact]
    public void Validate_RejectsWorksheetFormulasInOtherwiseApprovedWorkbook()
    {
        var path = Workbook("NDC", "Drug Name", "Price");
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet(1).Cell(2, 3).FormulaA1 = "=1+1";
            workbook.Save();
        }

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_formula_forbidden", error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Validate_RejectsDuplicateNdcIdentityHeadersRegardlessOfOrder(bool swap)
    {
        var path = swap
            ? Workbook("NDC11", "NDC", "Drug Name")
            : Workbook("NDC", "NDC11", "Drug Name");

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_ndc_identity_ambiguous", error.Code);
    }

    [Theory]
    [InlineData("xl/media/image1.png")]
    [InlineData("docProps/thumbnail.jpeg")]
    [InlineData("xl/printerSettings/printerSettings1.bin")]
    [InlineData("xl/binaryIndex.bin")]
    public void Validate_RejectsUnsupportedBinaryOrMediaParts(string partName)
    {
        var path = Workbook("NDC", "Drug Name", "Price");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry(partName);
            using var stream = entry.Open();
            stream.Write([0x50, 0x48, 0x49, 0x00]);
        }

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_unsupported_part_forbidden", error.Code);
    }

    [Fact]
    public void Validate_ScansRelationshipAttributeTextForPhi()
    {
        var path = Workbook("NDC", "Drug Name", "Price");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            const string partName = "xl/worksheets/_rels/sheet99.xml.rels";
            var entry = archive.CreateEntry(partName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="urn:internal" Target="patient_123456789" />
                </Relationships>
                """);
        }

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_phi_field_forbidden", error.Code);
    }

    [Fact]
    public void Validate_RejectsPhiFieldSplitAcrossRichTextRuns()
    {
        var path = Workbook("NDC", "Patient Name", "Price");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            Assert.NotNull(entry);
            string xml;
            using (var reader = new StreamReader(entry!.Open()))
                xml = reader.ReadToEnd();
            var textNode = Regex.Match(
                xml,
                @"<(?<prefix>[A-Za-z0-9]+:)?t[^>]*>Patient Name</(?:[A-Za-z0-9]+:)?t>",
                RegexOptions.CultureInvariant);
            Assert.True(textNode.Success);
            var prefix = textNode.Groups["prefix"].Value;
            entry.Delete();
            var replacement = archive.CreateEntry("xl/sharedStrings.xml");
            using var writer = new StreamWriter(replacement.Open());
            var splitXml = xml.Replace(
                textNode.Value,
                $"<{prefix}r><{prefix}t>Pat</{prefix}t></{prefix}r>" +
                $"<{prefix}r><{prefix}t>ient Name</{prefix}t></{prefix}r>",
                StringComparison.Ordinal);
            Assert.DoesNotContain("Patient Name", splitXml, StringComparison.Ordinal);
            writer.Write(splitXml);
        }

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_phi_field_forbidden", error.Code);
    }

    [Theory]
    [InlineData("xl/comments1.xml", "<comments><comment><text>Patient Name</text></comment></comments>", "xlsx_unsupported_part_forbidden")]
    [InlineData("xl/threadedComments/threadedComment1.xml", "<threadedComments><threadedComment text=\"Rx Number\" /></threadedComments>", "xlsx_unsupported_part_forbidden")]
    [InlineData("docProps/custom.xml", "<Properties><property name=\"Patient_Name\">value</property></Properties>", "xlsx_unsupported_part_forbidden")]
    [InlineData("xl/tables/table99.xml", "<table name=\"CustomerName\" displayName=\"CustomerName\" />", "xlsx_phi_field_forbidden")]
    public void Validate_RejectsPhiInNonWorksheetXmlParts(
        string partName,
        string xml,
        string expectedCode)
    {
        var path = Workbook("NDC", "Drug Name", "Price");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.CreateEntry(partName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(xml);
        }

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void Validate_RejectsPhiInWorkbookDefinedName()
    {
        var path = Workbook("NDC", "Drug Name", "Price");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("xl/workbook.xml");
            Assert.NotNull(entry);
            string xml;
            using (var reader = new StreamReader(entry!.Open()))
                xml = reader.ReadToEnd();
            entry.Delete();
            var replacement = archive.CreateEntry("xl/workbook.xml");
            using var writer = new StreamWriter(replacement.Open());
            var closing = Regex.Match(
                xml,
                @"</(?<prefix>[A-Za-z0-9]+:)?workbook>\s*$",
                RegexOptions.CultureInvariant);
            Assert.True(closing.Success);
            var prefix = closing.Groups["prefix"].Value;
            writer.Write(xml[..closing.Index] +
                $"<{prefix}definedNames><{prefix}definedName name=\"Patient_Name\">Pricing!$A$1</{prefix}definedName></{prefix}definedNames>" +
                closing.Value);
        }

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_phi_field_forbidden", error.Code);
    }

    [Fact]
    public void Validate_RejectsCsvRenamedAsXlsx()
    {
        var path = Path.Combine(_root, "not-a-workbook.xlsx");
        File.WriteAllText(path, "ndc,price\n1,2");

        Assert.ThrowsAny<InvalidDataException>(
            () => PricingWorkbookContentPolicy.Validate(path));
    }

    [Fact]
    public void Validate_RejectsArchiveAboveFourMiBProxyCeiling()
    {
        var path = Path.Combine(_root, "oversize.xlsx");
        File.WriteAllBytes(
            path,
            new byte[checked((int)PricingWorkbookContentPolicy.MaxArchiveBytes + 1)]);

        var error = Assert.Throws<PricingWorkbookContentException>(
            () => PricingWorkbookContentPolicy.Validate(path));

        Assert.Equal("xlsx_archive_size_invalid", error.Code);
    }

    private string Workbook(params string[] headers)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Pricing");
        for (var index = 0; index < headers.Length; index++)
        {
            sheet.Cell(1, index + 1).Value = headers[index];
            sheet.Cell(2, index + 1).Value = index == 0 ? "00000000000" : "value";
        }
        workbook.SaveAs(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
