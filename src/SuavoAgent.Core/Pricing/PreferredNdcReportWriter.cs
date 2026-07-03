using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Feature B (preferred-NDC-by-insurance) — B3 the report writer. Writes the READ-ONLY report Nadim
/// asked for ("run me a report and I'll do it manually"): one row per (medication, plan) with the
/// most-profitable NDC + the numbers behind it, so a pharmacist can set the PioneerRx preferred item by
/// hand. Produces a FRESH workbook (a deliverable), not a writeback into an input sheet — and it never
/// touches PioneerRx (the "putting" TOS risk lives only in the separate, default-OFF B4 writeback).
///
/// Cross-platform (ClosedXML, MIT) so the report can be built/tested off the box, exactly like
/// <see cref="ExcelPricingWriter"/>.
/// </summary>
public sealed class PreferredNdcReportWriter
{
    private readonly ILogger<PreferredNdcReportWriter> _logger;

    public PreferredNdcReportWriter(ILogger<PreferredNdcReportWriter> logger) => _logger = logger;

    /// <summary>Writes the report to <c>{outputDir}/preferred-ndc-report-{timestamp}.xlsx</c> and returns
    /// where it landed. <paramref name="timestamp"/> is passed in (callers stamp it) so the pure writer
    /// stays deterministic + testable. Money cells are numeric; empty for non-OK rows.</summary>
    public WriteResult Write(string outputDir, IReadOnlyList<PreferredNdcReportRow> rows, string timestamp)
    {
        try
        {
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, $"preferred-ndc-report-{timestamp}.xlsx");

            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Preferred NDC by Insurance");

            var headers = new[]
            {
                "Medication (drug group)", "Insurance plan", "Preferred NDC", "Manufacturer",
                "Acquisition cost", "Reimbursement", "Profit", "Δ vs next best",
                "Reimbursement basis", "Candidates", "Status",
            };
            for (var c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
            }

            int ok = 0, other = 0;
            var r = 2;
            foreach (var row in rows)
            {
                ws.Cell(r, 1).Value = row.DrugGroupKey;
                ws.Cell(r, 2).Value = row.PlanId;
                ws.Cell(r, 3).Value = row.PreferredNdc ?? "";
                ws.Cell(r, 4).Value = row.Manufacturer ?? "";
                SetMoney(ws.Cell(r, 5), row.AcquisitionCost);
                SetMoney(ws.Cell(r, 6), row.Reimbursement);
                SetMoney(ws.Cell(r, 7), row.Profit);
                SetMoney(ws.Cell(r, 8), row.DeltaOverRunnerUp);
                ws.Cell(r, 9).Value = BasisText(row.Basis);
                ws.Cell(r, 10).Value = row.CandidatesConsidered;
                ws.Cell(r, 11).Value = row.Status;
                if (row.Status == PreferredNdcStatus.Ok) ok++; else other++;
                r++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(outputPath);

            _logger.LogInformation("PreferredNdcReportWriter: wrote {Ok} recommended / {Other} flagged rows", ok, other);
            return WriteResult.Ok(outputPath, ok, other);
        }
        catch (Exception ex)
        {
            _logger.LogError("PreferredNdcReportWriter failed ({ErrorType})", ex.GetType().Name);
            return WriteResult.Fail("Preferred-NDC report write failed");
        }
    }

    private static void SetMoney(IXLCell cell, decimal? value)
    {
        if (value is { } v) { cell.Value = v; cell.Style.NumberFormat.Format = "0.0000"; }
        else cell.Value = "";
    }

    private static string BasisText(ReimbursementBasis basis) => basis switch
    {
        ReimbursementBasis.ContractOrMac => "contract/MAC (pre-claim)",
        ReimbursementBasis.AdjudicatedHistory => "estimate (recent paid claims)",
        _ => "unspecified",
    };
}
