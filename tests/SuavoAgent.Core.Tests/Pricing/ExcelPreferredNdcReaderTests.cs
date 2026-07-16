using System.IO.Compression;
using System.Security.Cryptography;
using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class ExcelPreferredNdcReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"suavo_pref_admission_{Guid.NewGuid():N}");

    public ExcelPreferredNdcReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Admits_exact_schema_and_carries_all_evidence_fields()
    {
        var path = Workbook();

        var admitted = PreferredNdcWorkbookAdmission.TryAdmit(path, out var lease, out var code);

        Assert.True(admitted, code);
        using var owned = lease!;
        Assert.Contains(("omeprazole-40", "PLAN-A"), owned.Reader.Pairs);
        var result = await owned.Reader.ReadCandidatesAsync(
            new PreferredNdcRequest("job", 7, "omeprazole-40", "PLAN-A"),
            default);
        Assert.True(result.Found);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("00093100001", candidate.Ndc);
        Assert.True(candidate.Available);
        Assert.True(candidate.Eligible);
        Assert.Equal(PreferredNdcAmountBasis.PerDispensedFill, candidate.AcquisitionAmountBasis);
        Assert.Equal(
            PreferredNdcEvidenceProvenance.PioneerRxAcquisitionCostExport,
            candidate.AcquisitionEvidenceProvenance);
        Assert.Equal(
            PreferredNdcEvidenceProvenance.PioneerRxContractOrMacExport,
            candidate.ReimbursementEvidenceProvenance);
        Assert.Equal(0, candidate.HistoricalSampleCount);
    }

    [Fact]
    public async Task Admission_reads_private_snapshot_and_source_change_cannot_change_results()
    {
        var path = Workbook();
        var originalHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
            .ToLowerInvariant();
        Assert.True(PreferredNdcWorkbookAdmission.TryAdmit(path, out var lease, out var code), code);
        using var owned = lease!;
        Assert.Equal(originalHash, owned.SourceSha256);

        using (var changed = new XLWorkbook(path))
        {
            changed.Worksheet(1).Cell(2, 3).SetValue("99999999999");
            changed.SaveAs(path);
        }

        var result = await owned.Reader.ReadCandidatesAsync(
            new PreferredNdcRequest("job", 0, "omeprazole-40", "PLAN-A"),
            default);
        Assert.Equal("00093100001", Assert.Single(result.Candidates).Ndc);
    }

    [Theory]
    [InlineData("Preferred NDC Candidate")]
    [InlineData("preferred NDC Candidates")]
    [InlineData("Sheet1")]
    public void Rejects_any_sheet_name_other_than_exact_contract(string sheetName)
    {
        var path = Workbook();
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet(1).Name = sheetName;
            workbook.SaveAs(path);
        }

        Assert.False(PreferredNdcWorkbookAdmission.TryAdmit(path, out _, out var code));
        Assert.Equal("xlsx_preferred_ndc_schema_forbidden", code);
    }

    [Theory]
    [InlineData("NDC11", "NDC11 code")]
    [InlineData("Insurance Plan ID", "Plan")]
    [InlineData("Expected Reimbursement", "Expected Reimbursement USD")]
    public void Rejects_substring_or_alias_headers(string exactHeader, string replacement)
    {
        var path = Workbook();
        ReplaceHeader(path, exactHeader, replacement);

        Assert.False(PreferredNdcWorkbookAdmission.TryAdmit(path, out _, out var code));
        Assert.Equal("xlsx_preferred_ndc_schema_forbidden", code);
    }

    [Fact]
    public void Rejects_duplicate_required_header_even_when_column_count_matches()
    {
        var path = Workbook();
        ReplaceHeader(path, "Manufacturer", "NDC11");

        Assert.False(PreferredNdcWorkbookAdmission.TryAdmit(path, out _, out var code));
        Assert.Equal("xlsx_preferred_ndc_schema_forbidden", code);
    }

    [Theory]
    [InlineData("00093-1000-01")]
    [InlineData("0009310001")]
    [InlineData("0009310000A")]
    [InlineData(" 00093100001")]
    public void Rejects_noncanonical_ndc_identity(string ndc)
    {
        var path = Workbook(ndc);

        Assert.False(PreferredNdcWorkbookAdmission.TryAdmit(path, out _, out var code));
        Assert.Equal("xlsx_preferred_ndc_data_forbidden", code);
    }

    [Fact]
    public void Rejects_duplicate_ndc_within_same_pair()
    {
        var path = Workbook();
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet(1);
            sheet.Row(2).CopyTo(sheet.Row(3));
            workbook.SaveAs(path);
        }

        Assert.False(PreferredNdcWorkbookAdmission.TryAdmit(path, out _, out var code));
        Assert.Equal("xlsx_preferred_ndc_duplicate_identity", code);
    }

    [Fact]
    public void Rejects_formula_before_reader_uses_cached_value()
    {
        var path = Workbook();
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet(1);
            var column = HeaderColumn(sheet, "Acquisition Amount");
            sheet.Cell(2, column).FormulaA1 = "=1+1";
            workbook.SaveAs(path);
        }

        Assert.False(PreferredNdcWorkbookAdmission.TryAdmit(path, out _, out var code));
        Assert.Equal("xlsx_formula_forbidden", code);
    }

    [Fact]
    public void Rejects_active_or_external_workbook_parts()
    {
        var path = Workbook();
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            archive.CreateEntry("xl/externalLinks/externalLink1.xml");

        Assert.False(PreferredNdcWorkbookAdmission.TryAdmit(path, out _, out var code));
        Assert.Equal("xlsx_active_content_forbidden", code);
    }

    [Fact]
    public void Rejects_phi_like_text_archive_wide_without_returning_it()
    {
        var path = Workbook();
        using (var workbook = new XLWorkbook(path))
        {
            var sheet = workbook.Worksheet(1);
            sheet.Cell(2, HeaderColumn(sheet, "Manufacturer")).SetValue("Patient Name");
            workbook.SaveAs(path);
        }

        Assert.False(PreferredNdcWorkbookAdmission.TryAdmit(path, out _, out var code));
        Assert.Equal("xlsx_phi_field_forbidden", code);
        Assert.DoesNotContain("Patient", code, StringComparison.OrdinalIgnoreCase);
    }

    private string Workbook(string ndc = "00093100001")
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(ExcelPreferredNdcReader.RequiredWorksheetName);
        for (var index = 0; index < ExcelPreferredNdcReader.RequiredHeaders.Length; index++)
            sheet.Cell(1, index + 1).SetValue(ExcelPreferredNdcReader.RequiredHeaders[index]);

        Set(sheet, "Drug Group Key", "omeprazole-40");
        Set(sheet, "Insurance Plan ID", "PLAN-A");
        Set(sheet, "NDC11", ndc);
        Set(sheet, "Manufacturer", "Example Labs");
        Set(sheet, "Acquisition Amount", "3.0000");
        Set(sheet, "Acquisition Amount Basis", "per_dispensed_fill");
        Set(sheet, "Expected Reimbursement", "11.0000");
        Set(sheet, "Reimbursement Amount Basis", "per_dispensed_fill");
        Set(sheet, "Available", "TRUE");
        Set(sheet, "Eligible", "TRUE");
        Set(sheet, "Reimbursement Basis", "contract_or_mac");
        Set(sheet, "Acquisition Evidence Provenance", "pioneerrx_acquisition_cost_export");
        Set(sheet, "Reimbursement Evidence Provenance", "pioneerrx_contract_or_mac_export");
        Set(sheet, "Acquisition Evidence As Of UTC", "2026-07-13T08:00:00Z");
        Set(sheet, "Reimbursement Evidence As Of UTC", "2026-07-13T08:00:00Z");
        Set(sheet, "Historical Sample Count", "0");
        workbook.SaveAs(path);
        return path;
    }

    private static void Set(IXLWorksheet sheet, string header, string value) =>
        sheet.Cell(2, HeaderColumn(sheet, header)).SetValue(value);

    private static int HeaderColumn(IXLWorksheet sheet, string header)
    {
        for (var column = 1; column <= ExcelPreferredNdcReader.RequiredHeaders.Length; column++)
        {
            if (string.Equals(sheet.Cell(1, column).GetString(), header, StringComparison.Ordinal))
                return column;
        }
        throw new InvalidOperationException("test header missing");
    }

    private static void ReplaceHeader(string path, string exactHeader, string replacement)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(1);
        sheet.Cell(1, HeaderColumn(sheet, exactHeader)).SetValue(replacement);
        workbook.SaveAs(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
