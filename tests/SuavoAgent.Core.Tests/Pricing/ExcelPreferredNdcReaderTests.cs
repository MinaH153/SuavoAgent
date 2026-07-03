using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>
/// Feature B Excel-in reader: makes the preferred-NDC report runnable from an exported candidate sheet
/// (before the live PioneerRx SQL read is mapped). Proves it groups candidates by (medication, plan),
/// parses money leniently, keeps rows with a missing number (engine fails closed on them), and reports
/// the distinct pairs as the report's row set.
/// </summary>
public sealed class ExcelPreferredNdcReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"suavo_pref_read_{Guid.NewGuid():N}");
    public ExcelPreferredNdcReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string BuildSheet()
    {
        var path = Path.Combine(_dir, "candidates.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Candidates");
        var headers = new[] { "Medication", "Insurance plan", "NDC", "Manufacturer", "Acquisition cost", "Reimbursement", "Status", "Basis" };
        for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
        var rows = new (string, string, string, string, string, string, string, string)[]
        {
            ("omeprazole", "PLAN-A", "00093-1-01", "Row1", "$8.00", "12.00", "Active", "contract"),
            ("omeprazole", "PLAN-A", "00093-3-01", "Best", "3.00", "11.00", "Active", "contract"),  // most profit
            ("lisinopril", "PLAN-A", "11111-1-01", "X", "4.00", "", "Active", "contract"),           // missing reimb
        };
        var r = 2;
        foreach (var (m, p, n, mf, cost, reimb, st, ba) in rows)
        {
            ws.Cell(r, 1).Value = m; ws.Cell(r, 2).Value = p; ws.Cell(r, 3).Value = n; ws.Cell(r, 4).Value = mf;
            ws.Cell(r, 5).Value = cost; ws.Cell(r, 6).Value = reimb; ws.Cell(r, 7).Value = st; ws.Cell(r, 8).Value = ba;
            r++;
        }
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public async Task Groups_candidates_by_pair_and_parses_money_leniently()
    {
        var reader = ExcelPreferredNdcReader.Load(BuildSheet());

        Assert.Contains(("omeprazole", "PLAN-A"), reader.Pairs);
        Assert.Contains(("lisinopril", "PLAN-A"), reader.Pairs);

        var oma = await reader.ReadCandidatesAsync(new PreferredNdcRequest("j", 0, "omeprazole", "PLAN-A"), default);
        Assert.True(oma.Found);
        Assert.Equal(2, oma.Candidates.Count);
        Assert.Equal(ReimbursementBasis.ContractOrMac, oma.Basis);
        Assert.Contains(oma.Candidates, c => c.Ndc == "00093-1-01" && c.AcquisitionCost == 8.00m); // "$8.00" parsed

        var lis = await reader.ReadCandidatesAsync(new PreferredNdcRequest("j", 1, "lisinopril", "PLAN-A"), default);
        Assert.True(lis.Found);
        Assert.Null(lis.Candidates[0].Reimbursement);   // kept, missing number → engine fails closed downstream
    }

    [Fact]
    public async Task Downgrades_basis_to_unspecified_when_rows_in_a_pair_disagree()
    {
        // Same (drug, plan) with conflicting Basis cells → don't brand the winner with row 1's basis.
        var path = Path.Combine(_dir, "conflict.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("C");
            foreach (var (c, h) in new[] { (1, "Medication"), (2, "Plan"), (3, "NDC"), (4, "Acquisition"), (5, "Reimbursement"), (6, "Basis") })
                ws.Cell(1, c).Value = h;
            ws.Cell(2, 1).Value = "atorvastatin"; ws.Cell(2, 2).Value = "PLAN-A"; ws.Cell(2, 3).Value = "1-1-1"; ws.Cell(2, 4).Value = "3.00"; ws.Cell(2, 5).Value = "12.00"; ws.Cell(2, 6).Value = "contract";
            ws.Cell(3, 1).Value = "atorvastatin"; ws.Cell(3, 2).Value = "PLAN-A"; ws.Cell(3, 3).Value = "2-2-2"; ws.Cell(3, 4).Value = "4.00"; ws.Cell(3, 5).Value = "40.00"; ws.Cell(3, 6).Value = "adjudicated estimate";
            wb.SaveAs(path);
        }
        var reader = ExcelPreferredNdcReader.Load(path);
        var res = await reader.ReadCandidatesAsync(new PreferredNdcRequest("j", 0, "atorvastatin", "PLAN-A"), default);
        Assert.True(res.Found);
        Assert.Equal(2, res.Candidates.Count);
        Assert.Equal(ReimbursementBasis.Unspecified, res.Basis);   // conflict -> fail closed, not "contract"
    }

    [Fact]
    public async Task Reports_pair_not_in_sheet_for_an_unknown_pair()
    {
        var reader = ExcelPreferredNdcReader.Load(BuildSheet());
        var res = await reader.ReadCandidatesAsync(new PreferredNdcRequest("j", 0, "unknown", "PLAN-Z"), default);
        Assert.False(res.Found);
        Assert.Equal("pair_not_in_sheet", res.ErrorMessage);
    }
}
