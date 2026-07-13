using System.Security.Cryptography;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests;

public sealed class PreferredNdcReportCompositionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"suavo_pref_compose_{Guid.NewGuid():N}");

    public PreferredNdcReportCompositionServiceTests() => Directory.CreateDirectory(_root);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Composes_admitted_snapshot_to_fresh_read_only_report_without_mutating_source()
    {
        var source = Workbook();
        var before = SHA256.HashData(File.ReadAllBytes(source));
        var service = Service();

        var result = await service.ComposeAsync(source, _root, default);

        Assert.True(result.Success, result.Code);
        Assert.Equal("report_written", result.Code);
        Assert.Equal(1, result.PairCount);
        Assert.Equal(1, result.RecommendationCount);
        Assert.Equal(0, result.ManualReviewCount);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(source)));

        using var workbook = new XLWorkbook(result.OutputPath!);
        var sheet = workbook.Worksheet(1);
        Assert.Equal("00093100001", sheet.Cell(2, 3).GetString());
        Assert.Equal("OK", sheet.Cell(2, 17).GetString());
    }

    [Fact]
    public async Task Same_clock_tick_refuses_overwrite_with_fixed_code_and_no_partial_final()
    {
        var source = Workbook();
        var service = Service();

        var first = await service.ComposeAsync(source, _root, default);
        var second = await service.ComposeAsync(source, _root, default);

        Assert.True(first.Success, first.Code);
        Assert.False(second.Success);
        Assert.Equal(PreferredNdcReportWriter.OutputExistsError, second.Code);
        Assert.Single(Directory.GetFiles(_root, "preferred-ndc-report-*.xlsx"));
        Assert.Empty(Directory.GetFiles(_root, ".preferred-ndc-report-*.tmp"));
    }

    private PreferredNdcReportCompositionService Service() =>
        new(
            NullLogger<PreferredNdcReportWriter>.Instance,
            new FixedTimeProvider(Now));

    private string Workbook()
    {
        var path = Path.Combine(_root, "input.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Preferred NDC Candidates");
        var headers = new[]
        {
            "Drug Group Key", "Insurance Plan ID", "NDC11", "Manufacturer",
            "Acquisition Amount", "Acquisition Amount Basis", "Expected Reimbursement",
            "Reimbursement Amount Basis", "Available", "Eligible", "Reimbursement Basis",
            "Acquisition Evidence Provenance", "Reimbursement Evidence Provenance",
            "Acquisition Evidence As Of UTC", "Reimbursement Evidence As Of UTC",
            "Historical Sample Count",
        };
        for (var index = 0; index < headers.Length; index++)
            sheet.Cell(1, index + 1).SetValue(headers[index]);
        var values = new[]
        {
            "omeprazole-40", "PLAN-A", "00093100001", "Example Labs",
            "3.0000", "per_dispensed_fill", "11.0000", "per_dispensed_fill",
            "TRUE", "TRUE", "contract_or_mac", "pioneerrx_acquisition_cost_export",
            "pioneerrx_contract_or_mac_export", "2026-07-13T08:00:00Z",
            "2026-07-13T08:00:00Z", "0",
        };
        for (var index = 0; index < values.Length; index++)
            sheet.Cell(2, index + 1).SetValue(values[index]);
        workbook.SaveAs(path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
