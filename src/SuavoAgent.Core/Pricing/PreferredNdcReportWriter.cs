using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Feature B (preferred-NDC-by-insurance) — B3 the report writer. Writes the READ-ONLY report Nadim
/// asked for ("run me a report and I'll do it manually"): one row per (medication, plan) with the
/// highest expected gross-margin proxy + the numbers behind it, so a pharmacist can review the result
/// before setting the PioneerRx preferred item by hand. The proxy is reimbursement minus acquisition
/// only; it is not labeled as net profit because downstream fees, reversals, rebates, and clawbacks are
/// not yet mapped. Produces a FRESH workbook (a deliverable), not a writeback into an input sheet — and it never
/// touches PioneerRx (the "putting" TOS risk lives only in the separate, default-OFF B4 writeback).
///
/// Cross-platform (ClosedXML, MIT) so the report can be built/tested off the box, exactly like
/// <see cref="ExcelPricingWriter"/>.
/// </summary>
public sealed class PreferredNdcReportWriter
{
    public const string OutputExistsError = "preferred_ndc_report_already_exists";
    public const string WriteFailedError = "preferred_ndc_report_write_failed";

    private readonly ILogger<PreferredNdcReportWriter> _logger;

    public PreferredNdcReportWriter(ILogger<PreferredNdcReportWriter> logger) => _logger = logger;

    /// <summary>Writes the report to <c>{outputDir}/preferred-ndc-report-{timestamp}.xlsx</c> and returns
    /// where it landed. <paramref name="timestamp"/> is passed in (callers stamp it) so the pure writer
    /// stays deterministic + testable. Money cells are numeric; empty for non-OK rows.</summary>
    public WriteResult Write(string outputDir, IReadOnlyList<PreferredNdcReportRow> rows, string timestamp)
    {
        string? temporaryPath = null;
        try
        {
            if (string.IsNullOrWhiteSpace(outputDir) || rows is null ||
                timestamp.Length is < 1 or > 32 ||
                timestamp.Any(character =>
                    character is not (>= '0' and <= '9' or >= 'A' and <= 'Z' or
                        >= 'a' and <= 'z' or '-' or '_')))
                return WriteResult.Fail("Preferred-NDC report arguments invalid");

            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, $"preferred-ndc-report-{timestamp}.xlsx");
            if (File.Exists(outputPath))
                return WriteResult.Fail(OutputExistsError);

            using var wb = new XLWorkbook();
            var ws = wb.AddWorksheet("Preferred NDC by Insurance");

            var headers = new[]
            {
                "Medication (drug group)", "Insurance plan", "Preferred NDC", "Manufacturer",
                "Acquisition cost", "Reimbursement",
                "Expected gross-margin proxy (reimbursement - acquisition)",
                "Δ vs next-best proxy",
                "Amount basis", "Reimbursement basis", "Acquisition evidence provenance",
                "Reimbursement evidence provenance", "Acquisition evidence as of UTC",
                "Reimbursement evidence as of UTC", "Historical sample count", "Candidates", "Status",
                "Calculation scope",
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
                ws.Cell(r, 9).Value = AmountBasisText(row.AmountBasis);
                ws.Cell(r, 10).Value = BasisText(row.Basis);
                ws.Cell(r, 11).Value = ProvenanceText(row.AcquisitionEvidenceProvenance);
                ws.Cell(r, 12).Value = ProvenanceText(row.ReimbursementEvidenceProvenance);
                ws.Cell(r, 13).Value = row.AcquisitionEvidenceAsOfUtc?.ToUniversalTime()
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) ?? "";
                ws.Cell(r, 14).Value = row.ReimbursementEvidenceAsOfUtc?.ToUniversalTime()
                    .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) ?? "";
                if (row.HistoricalSampleCount is { } sampleCount)
                    ws.Cell(r, 15).Value = sampleCount;
                else
                    ws.Cell(r, 15).Value = "";
                ws.Cell(r, 16).Value = row.CandidatesConsidered;
                ws.Cell(r, 17).Value = row.Status;
                ws.Cell(r, 18).Value =
                    "Gross-margin proxy only; excludes downstream fees, DIR/clawbacks, rebates, reversals, taxes, and dispensing overhead.";
                if (row.Status == PreferredNdcStatus.Ok) ok++; else other++;
                r++;
            }

            ws.Columns().AdjustToContents();
            temporaryPath = Path.Combine(
                outputDir,
                $".preferred-ndc-report-{Guid.NewGuid():N}.tmp");
            using (var output = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                wb.SaveAs(output);
                output.Flush(flushToDisk: true);
            }
            try
            {
                File.Move(temporaryPath, outputPath, overwrite: false);
                temporaryPath = null;
            }
            catch (IOException) when (File.Exists(outputPath))
            {
                return WriteResult.Fail(OutputExistsError);
            }

            _logger.LogInformation("PreferredNdcReportWriter: wrote {Ok} recommended / {Other} flagged rows", ok, other);
            return WriteResult.Ok(outputPath, ok, other);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "core.preferred_ndc_writer.failed exception_type={ExceptionType}",
                ex.GetType().Name);
            return WriteResult.Fail(WriteFailedError);
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { /* best-effort private temp cleanup */ }
            }
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

    private static string AmountBasisText(PreferredNdcAmountBasis basis) => basis switch
    {
        PreferredNdcAmountBasis.PerDispensedFill => "per dispensed fill",
        PreferredNdcAmountBasis.PerPackage => "per package",
        PreferredNdcAmountBasis.PerUnit => "per unit",
        _ => "unspecified",
    };

    private static string ProvenanceText(PreferredNdcEvidenceProvenance provenance) => provenance switch
    {
        PreferredNdcEvidenceProvenance.PioneerRxAcquisitionCostExport => "PioneerRx acquisition-cost export",
        PreferredNdcEvidenceProvenance.PioneerRxContractOrMacExport => "PioneerRx contract/MAC export",
        PreferredNdcEvidenceProvenance.PioneerRxAdjudicatedClaimsExport => "PioneerRx adjudicated claims export",
        _ => "unspecified",
    };
}
