using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Feature B — an EXCEL-IN reader (an <see cref="IPreferredNdcDataSource"/>). It makes the preferred-NDC
/// report RUNNABLE TODAY without the box: the pharmacist exports (or the agent later reads) a candidate
/// sheet — one row per candidate NDC with its acquisition cost + expected reimbursement under a plan —
/// and this reader groups it by (medication, plan) so <c>PreferredNdcReportRunner</c> can pick the
/// most-profitable NDC per pair. The live PioneerRx SQL/UIA reader (B2) is the same seam, added after the
/// box recon maps the reimbursement tables; the report format + engine are identical either way.
///
/// <para>Read-only by construction (it only reads a file). Columns are matched by header name
/// (case-insensitive, substring) so the exact export layout can vary. A row missing a cost or
/// reimbursement is still surfaced (nullable) — the engine fails closed on it rather than the reader
/// dropping it silently.</para>
///
/// Expected headers (any order): Drug/Medication, Plan/Insurance, NDC, Manufacturer,
/// Acquisition (cost), Reimbursement, Status, [Basis].
/// </summary>
public sealed class ExcelPreferredNdcReader : IPreferredNdcDataSource
{
    private readonly Dictionary<string, (List<PreferredNdcCandidate> Cands, ReimbursementBasis Basis)> _byPair;

    private ExcelPreferredNdcReader(Dictionary<string, (List<PreferredNdcCandidate>, ReimbursementBasis)> byPair)
        => _byPair = byPair;

    /// <summary>The distinct (medication, plan) pairs found in the sheet — the report's row set.</summary>
    public IReadOnlyList<(string DrugGroupKey, string PlanId)> Pairs { get; private set; } = new List<(string, string)>();

    public Task<PreferredNdcReadResult> ReadCandidatesAsync(PreferredNdcRequest r, CancellationToken ct)
    {
        var key = Key(r.DrugGroupKey, r.PlanId);
        if (_byPair.TryGetValue(key, out var hit) && hit.Cands.Count > 0)
            return Task.FromResult(new PreferredNdcReadResult(
                r.JobId, r.RowIndex, r.DrugGroupKey, r.PlanId, true, hit.Cands, hit.Basis, null));
        return Task.FromResult(new PreferredNdcReadResult(
            r.JobId, r.RowIndex, r.DrugGroupKey, r.PlanId, false,
            new List<PreferredNdcCandidate>(), ReimbursementBasis.Unspecified, "pair_not_in_sheet"));
    }

    /// <summary>Loads the candidate sheet. Throws only on an unreadable/empty workbook; a row with a bad
    /// number is kept with a null value (engine fails closed) rather than aborting the load.</summary>
    public static ExcelPreferredNdcReader Load(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;

        int drug = -1, plan = -1, ndc = -1, mfr = -1, cost = -1, reimb = -1, status = -1, basis = -1;
        for (var c = 1; c <= lastCol; c++)
        {
            var h = ws.Cell(1, c).GetString().Trim().ToLowerInvariant();
            if (drug == -1 && (h.Contains("drug") || h.Contains("medication"))) drug = c;
            else if (plan == -1 && (h.Contains("plan") || h.Contains("insurance"))) plan = c;
            else if (ndc == -1 && h.Contains("ndc")) ndc = c;
            else if (mfr == -1 && (h.Contains("manufacturer") || h.Contains("mfr"))) mfr = c;
            else if (cost == -1 && (h.Contains("acquisition") || h.Contains("cost"))) cost = c;
            else if (reimb == -1 && h.Contains("reimb")) reimb = c;
            else if (status == -1 && h.Contains("status")) status = c;
            else if (basis == -1 && h.Contains("basis")) basis = c;
        }
        if (drug == -1 || plan == -1 || ndc == -1)
            throw new InvalidOperationException("candidate sheet is missing a Drug, Plan, or NDC column");

        var byPair = new Dictionary<string, (List<PreferredNdcCandidate>, ReimbursementBasis)>(StringComparer.Ordinal);
        var pairs = new List<(string, string)>();
        for (var r = 2; r <= lastRow; r++)
        {
            var d = ws.Cell(r, drug).GetString().Trim();
            var p = ws.Cell(r, plan).GetString().Trim();
            var n = ws.Cell(r, ndc).GetString().Trim();
            if (d.Length == 0 || p.Length == 0 || n.Length == 0) continue;

            var cand = new PreferredNdcCandidate(
                n,
                mfr > 0 ? ws.Cell(r, mfr).GetString().Trim() : "",
                cost > 0 ? ParseMoney(ws.Cell(r, cost).GetString()) : null,
                reimb > 0 ? ParseMoney(ws.Cell(r, reimb).GetString()) : null,
                status > 0 ? ws.Cell(r, status).GetString().Trim() : "");

            var key = Key(d, p);
            var rowBasis = ReadBasis(basis > 0 ? ws.Cell(r, basis).GetString() : "");
            if (!byPair.TryGetValue(key, out var bucket))
            {
                bucket = (new List<PreferredNdcCandidate>(), rowBasis);
                pairs.Add((d, p));
            }
            else if (bucket.Item2 != rowBasis)
            {
                // Rows in the same pair disagree on provenance → fail closed to Unspecified rather than
                // branding the winner's number with row 1's basis (contract vs. estimate must not be faked).
                bucket = (bucket.Item1, ReimbursementBasis.Unspecified);
            }
            bucket.Item1.Add(cand);
            byPair[key] = bucket;
        }

        return new ExcelPreferredNdcReader(byPair) { Pairs = pairs };
    }

    private static string Key(string drug, string plan) => drug + "\u0001" + plan;

    private static decimal? ParseMoney(string? s) =>
        ProfitOptimizerMoney.TryParse(s, out var v) ? v : (decimal?)null;

    private static ReimbursementBasis ReadBasis(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s.Contains("contract") || s.Contains("mac")) return ReimbursementBasis.ContractOrMac;
        if (s.Contains("claim") || s.Contains("adjud") || s.Contains("estimate")) return ReimbursementBasis.AdjudicatedHistory;
        return ReimbursementBasis.Unspecified;
    }
}

/// <summary>Money parse identical to ProfitOptimizer.TryParseMoney, duplicated here to keep Core free of a
/// Helper dependency (ProfitOptimizer lives in Helper). Same lenient rules ($ + thousands tolerated).</summary>
internal static class ProfitOptimizerMoney
{
    public static bool TryParse(string? text, out decimal value) =>
        decimal.TryParse((text ?? "").Trim().TrimStart('$'),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out value);
}
