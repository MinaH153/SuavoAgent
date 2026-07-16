using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>
/// Feature B B3 report writer: turns the margin-proxy rows into the READ-ONLY Excel report Nadim
/// asked for. Round-trips a written report back out to prove the numbers + statuses land in the right
/// cells — the deliverable the pharmacist reads to set the PioneerRx preferred item by hand.
/// </summary>
public sealed class PreferredNdcReportWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"suavo_pref_ndc_{Guid.NewGuid():N}");
    public PreferredNdcReportWriterTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Writes_the_report_with_the_winner_numbers_and_flags_non_ok_rows()
    {
        var rows = new[]
        {
            new PreferredNdcReportRow("omeprazole-40", "PLAN-A", PreferredNdcStatus.Ok,
                "00093300001", "Best Labs", 3.00m, 11.00m, 8.00m, 3.00m,
                ReimbursementBasis.ContractOrMac, PreferredNdcAmountBasis.PerDispensedFill,
                PreferredNdcEvidenceProvenance.PioneerRxAcquisitionCostExport,
                PreferredNdcEvidenceProvenance.PioneerRxContractOrMacExport,
                new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero), 0, 3),
            new PreferredNdcReportRow("lisinopril-20", "PLAN-A", PreferredNdcStatus.NoEligible,
                null, null, null, null, null, null, ReimbursementBasis.Unspecified,
                PreferredNdcAmountBasis.Unspecified, PreferredNdcEvidenceProvenance.Unspecified,
                PreferredNdcEvidenceProvenance.Unspecified, null, null, null, 2),
        };

        var writer = new PreferredNdcReportWriter(NullLogger<PreferredNdcReportWriter>.Instance);
        var res = writer.Write(_dir, rows, "20260703-021500");

        Assert.True(res.Success, res.Error);
        Assert.Equal(1, res.OkRows);
        Assert.Equal(1, res.FailRows);
        Assert.Contains("preferred-ndc-report-20260703-021500", res.OutputPath);

        using var wb = new XLWorkbook(res.OutputPath!);
        var ws = wb.Worksheet(1);
        int Col(string name) { for (int c = 1; c <= 20; c++) if (ws.Cell(1, c).GetString().Contains(name, StringComparison.OrdinalIgnoreCase)) return c; return -1; }
        int ndc = Col("Preferred NDC"), margin = Col("gross-margin proxy"), status = Col("Status"), delta = Col("next-best proxy");
        int amountBasis = Col("Amount basis"), provenance = Col("Reimbursement evidence provenance"), samples = Col("Historical sample");
        int scope = Col("Calculation scope");

        // OK row: winner + numbers present.
        Assert.Equal("00093300001", ws.Cell(2, ndc).GetString());
        Assert.Equal(8.00, ws.Cell(2, margin).GetDouble(), 4);
        Assert.Equal(3.00, ws.Cell(2, delta).GetDouble(), 4);
        Assert.Equal("OK", ws.Cell(2, status).GetString());
        Assert.Equal("per dispensed fill", ws.Cell(2, amountBasis).GetString());
        Assert.Equal("PioneerRx contract/MAC export", ws.Cell(2, provenance).GetString());
        Assert.Equal(0, ws.Cell(2, samples).GetDouble());
        Assert.Contains("excludes downstream fees", ws.Cell(2, scope).GetString());
        // Flagged row: no NDC, no margin proxy, explicit status.
        Assert.Equal("", ws.Cell(3, ndc).GetString());
        Assert.Equal("", ws.Cell(3, margin).GetString());
        Assert.Equal(PreferredNdcStatus.NoEligible, ws.Cell(3, status).GetString());
    }

    [Fact]
    public void Same_timestamp_refuses_overwrite_and_leaves_no_partial_file()
    {
        var writer = new PreferredNdcReportWriter(NullLogger<PreferredNdcReportWriter>.Instance);
        var rows = new[]
        {
            new PreferredNdcReportRow(
                "drug", "plan", PreferredNdcStatus.NoEligible,
                null, null, null, null, null, null, ReimbursementBasis.Unspecified,
                PreferredNdcAmountBasis.Unspecified,
                PreferredNdcEvidenceProvenance.Unspecified,
                PreferredNdcEvidenceProvenance.Unspecified,
                null, null, null, 0),
        };

        var first = writer.Write(_dir, rows, "fixed");
        var second = writer.Write(_dir, rows, "fixed");

        Assert.True(first.Success, first.Error);
        Assert.False(second.Success);
        Assert.Equal(PreferredNdcReportWriter.OutputExistsError, second.Error);
        Assert.Single(Directory.GetFiles(_dir, "*.xlsx"));
        Assert.Empty(Directory.GetFiles(_dir, ".preferred-ndc-report-*.tmp"));
    }
}
