using System.Security.Cryptography;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class GooglePricingWorkbookNormalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"suavo_google_pricing_{Guid.NewGuid():N}");

    public GooglePricingWorkbookNormalizationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Prepare_NormalizesExactGoogleWrapperToStrictValuesOnlyWorkbook()
    {
        var source = GooglePricingWorkbookFixture.Create(_root);
        var sourceDigest = SHA256.HashData(File.ReadAllBytes(source));
        Assert.False(PricingWorkbookContentPolicy.TryValidateForExecution(source, out _));

        var prepared = PricingWorkbookContentPolicy.TryPrepareForExecution(
            source, out var lease, out var code);

        Assert.True(prepared, code);
        Assert.NotNull(lease);
        string normalizedPath;
        using (lease!)
        {
            normalizedPath = lease.WorkbookPath;
            Assert.True(lease.WasNormalized);
            Assert.NotEqual(source, normalizedPath);
            PricingWorkbookContentPolicy.Validate(normalizedPath);

            var read = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance).Read(
                normalizedPath,
                "NDC",
                baselineCostColumnHint: "Acquisition Cost",
                quantityColumnHint: "Total Dispensed");
            Assert.True(read.Success, read.Error);
            Assert.Equal(2, read.Rows.Count);
            Assert.Empty(read.Invalid);
            Assert.All(read.Rows, row => Assert.Null(row.BaselineCostPerUnit));
            Assert.All(read.Rows, row => Assert.Null(row.Quantity));
        }

        Assert.False(File.Exists(normalizedPath));
        Assert.Equal(sourceDigest, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public void Prepare_StrictWorkbookAlwaysUsesPrivateImmutableSnapshot()
    {
        var source = Path.Combine(_root, $"{Guid.NewGuid():N}.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.AddWorksheet("Pricing");
            sheet.Cell(1, 1).Value = "NDC";
            sheet.Cell(2, 1).SetValue("55111064501");
            workbook.SaveAs(source);
        }
        var sourceDigest = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant();

        var prepared = PricingWorkbookContentPolicy.TryPrepareForExecution(
            source, out var lease, out var code);

        Assert.True(prepared, code);
        Assert.NotNull(lease);
        string snapshotPath;
        using (lease!)
        {
            snapshotPath = lease.WorkbookPath;
            Assert.False(lease.WasNormalized);
            Assert.NotEqual(source, snapshotPath);
            Assert.Equal(sourceDigest, lease.SourceSha256);

            // Admission owns a byte-for-byte snapshot. Later changes to the
            // operator's live workbook cannot alter the execution input.
            using (var changed = new XLWorkbook(source))
            {
                changed.Worksheet(1).Cell(2, 1).SetValue("00093512401");
                changed.SaveAs(source);
            }
            var read = new ExcelPricingReader(
                NullLogger<ExcelPricingReader>.Instance).Read(snapshotPath);
            Assert.Equal("55111064501", Assert.Single(read.Rows).NdcNormalized);
        }

        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public void Prepare_PreservesPopulatedBlankNdcRowForManualReview()
    {
        var source = GooglePricingWorkbookFixture.Create(
            _root, withPopulatedBlankNdcRow: true);

        var prepared = PricingWorkbookContentPolicy.TryPrepareForExecution(
            source, out var lease, out var code);

        Assert.True(prepared, code);
        using var execution = lease!;
        {
            var read = new ExcelPricingReader(
                NullLogger<ExcelPricingReader>.Instance).Read(execution.WorkbookPath);
            Assert.True(read.Success, read.Error);
            Assert.Equal(2, read.Rows.Count);
            var invalid = Assert.Single(read.Invalid);
            Assert.Equal("blank_ndc_on_populated_row", invalid.Reason);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Prepare_RejectsNonExactDrawingOrOpaqueMetadata(
        bool drawingPayload,
        bool unknownMetadata)
    {
        var source = GooglePricingWorkbookFixture.Create(
            _root,
            withDrawingPayload: drawingPayload,
            withUnknownMetadata: unknownMetadata);

        var prepared = PricingWorkbookContentPolicy.TryPrepareForExecution(
            source, out var lease, out var code);

        Assert.False(prepared);
        Assert.Null(lease);
        Assert.Equal("xlsx_google_wrapper_profile_forbidden", code);
    }

    [Fact]
    public void Prepare_RejectsFormulaBeforeAnyValuesAreCopied()
    {
        var source = GooglePricingWorkbookFixture.Create(_root, withFormula: true);

        var prepared = PricingWorkbookContentPolicy.TryPrepareForExecution(
            source, out var lease, out var code);

        Assert.False(prepared);
        Assert.Null(lease);
        Assert.Equal("xlsx_formula_forbidden", code);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
