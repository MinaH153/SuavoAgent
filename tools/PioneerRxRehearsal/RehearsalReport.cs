using ClosedXML.Excel;
using PioneerRxSim;
using SuavoAgent.Core.Pricing;

namespace PioneerRxRehearsal;

public sealed record RowVerdict(
    int RowIndex,
    string RawNdc,
    string Key,
    string Status,
    string Supplier,
    string Cost,
    NdcExpectation? Expectation,
    bool Pass,
    string Detail);

/// <summary>
/// Grades the priced sibling workbook against SimCatalog's hand-written expectations.
/// MustMatch mismatches fail the run (exit 2); Probe rows never fail — their observed
/// outcome is printed as a graded FINDING (the probe IS the deliverable).
/// </summary>
public static class RehearsalReport
{
    public static int Evaluate(
        string sourcePath,
        string outputPath,
        IReadOnlyList<NdcExpectation> expectations,
        TextWriter @out)
    {
        var expectedByKey = expectations.ToDictionary(e => e.Ndc11, e => e, StringComparer.OrdinalIgnoreCase);

        using var wb = new XLWorkbook(outputPath);
        var ws = wb.Worksheets.First();
        var lastRow = ws.LastRowUsed()!.RowNumber();
        var lastCol = ws.LastColumnUsed()!.ColumnNumber();

        int ndcCol = FindCol(ws, "NDC", lastCol);
        int supplierCol = FindCol(ws, "Best Supplier", lastCol);
        int costCol = FindCol(ws, "Best Cost", lastCol);
        int statusCol = FindCol(ws, ExcelPricingWriter.DefaultStatusHeader, lastCol);

        if (ndcCol < 1 || supplierCol < 1 || costCol < 1 || statusCol < 1)
        {
            @out.WriteLine("CONFORMANCE FAIL: priced workbook is missing one of the writeback columns " +
                           $"(NDC={ndcCol}, Best Supplier={supplierCol}, Best Cost={costCol}, Status={statusCol}). " +
                           "The sibling-file writeback contract is broken.");
            return 2;
        }

        var verdicts = new List<RowVerdict>();
        for (var r = 2; r <= lastRow; r++)
        {
            var raw = ws.Cell(r, ndcCol).GetString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(raw)) continue;

            // Same canonicalization the chain itself uses; unparseable rows key by raw text.
            var normalized = NdcNormalizer.Normalize(raw);
            var key = normalized.Ok && normalized.Canonical11 is not null ? normalized.Canonical11 : raw;

            var status = ws.Cell(r, statusCol).GetString()?.Trim() ?? "";
            var supplier = ws.Cell(r, supplierCol).GetString()?.Trim() ?? "";
            var cost = ws.Cell(r, costCol).GetString()?.Trim() ?? "";

            expectedByKey.TryGetValue(key, out var exp);
            var (pass, detail) = Grade(exp, status, supplier, cost);
            verdicts.Add(new RowVerdict(r, raw, key, status, supplier, cost, exp, pass, detail));
        }

        return Print(verdicts, @out);
    }

    private static (bool Pass, string Detail) Grade(NdcExpectation? exp, string status, string supplier, string cost)
    {
        if (exp is null)
            return (true, "no expectation registered for this NDC (pad row?) — not graded");

        var statusOk = exp.ExpectedStatus.EndsWith('…')
            ? status.StartsWith(exp.ExpectedStatus[..^1], StringComparison.Ordinal)
            : string.Equals(status, exp.ExpectedStatus, StringComparison.Ordinal);

        var supplierOk = exp.ExpectedSupplier is null
            || string.Equals(supplier, exp.ExpectedSupplier, StringComparison.Ordinal);

        var costOk = exp.ExpectedCost is null
            || (decimal.TryParse(cost, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                && Math.Abs(parsed - exp.ExpectedCost.Value) < 0.00005m);

        if (statusOk && supplierOk && costOk)
            return (true, "matches expectation");

        var parts = new List<string>();
        if (!statusOk) parts.Add($"status '{status}' ≠ '{exp.ExpectedStatus}'");
        if (!supplierOk) parts.Add($"supplier '{supplier}' ≠ '{exp.ExpectedSupplier}'");
        if (!costOk) parts.Add($"cost '{cost}' ≠ {exp.ExpectedCost}");
        return (false, string.Join("; ", parts));
    }

    private static int Print(List<RowVerdict> verdicts, TextWriter @out)
    {
        @out.WriteLine();
        @out.WriteLine("── Row-by-row verdicts ──────────────────────────────────────────────────────");
        @out.WriteLine($"{"Row",-4} {"NDC (raw)",-14} {"Status",-46} {"Best Supplier",-40} {"Cost",-10} Verdict");

        var conformanceFailures = 0;
        var findings = new List<string>();

        foreach (var v in verdicts)
        {
            string verdict;
            if (v.Expectation is null)
            {
                verdict = "—";
            }
            else if (v.Expectation.Kind == ExpectationKind.MustMatch)
            {
                verdict = v.Pass ? "PASS" : "FAIL";
                if (!v.Pass) conformanceFailures++;
            }
            else // Probe — grade and report, never fail the run
            {
                if (v.Pass)
                {
                    verdict = "PROBE✓";
                    findings.Add(
                        $"PROBE PASSED (row {v.RowIndex}, NDC {v.RawNdc}): workflow behaved correctly — {v.Expectation.Proves}");
                }
                else
                {
                    verdict = "PROBE!";
                    var critical = string.Equals(v.Status, "OK", StringComparison.Ordinal);
                    findings.Add(
                        $"{(critical ? "CRITICAL FINDING" : "FINDING")} (row {v.RowIndex}, NDC {v.RawNdc}): " +
                        $"observed status='{v.Status}' supplier='{v.Supplier}' cost='{v.Cost}' " +
                        $"(correct would be '{v.Expectation.ExpectedStatus}').\n" +
                        $"    {v.Expectation.GapNote ?? v.Expectation.Proves}");
                }
            }

            @out.WriteLine($"{v.RowIndex,-4} {Trunc(v.RawNdc, 14),-14} {Trunc(v.Status, 46),-46} {Trunc(v.Supplier, 40),-40} {Trunc(v.Cost, 10),-10} {verdict}");
            if (v.Expectation is { Kind: ExpectationKind.MustMatch } && !v.Pass)
                @out.WriteLine($"     └─ {v.Detail}");
        }

        @out.WriteLine();
        @out.WriteLine("── Findings (probes + gaps for the pre-Nadim fix list) ─────────────────────");
        if (findings.Count == 0) @out.WriteLine("(none)");
        foreach (var f in findings) @out.WriteLine("• " + f);

        @out.WriteLine();
        var graded = verdicts.Count(v => v.Expectation is { Kind: ExpectationKind.MustMatch });
        var passed = verdicts.Count(v => v.Expectation is { Kind: ExpectationKind.MustMatch } && v.Pass);
        @out.WriteLine($"── Summary: {passed}/{graded} MustMatch rows passed; " +
                       $"{verdicts.Count(v => v.Expectation?.Kind == ExpectationKind.Probe)} probe(s) reported above.");
        @out.WriteLine(conformanceFailures == 0
            ? "REHEARSAL RESULT: CONFORMANCE PASS — the real workflow drove the PioneerRx-shaped surface end-to-end."
            : $"REHEARSAL RESULT: CONFORMANCE FAIL — {conformanceFailures} MustMatch row(s) diverged. " +
              "If the sim surface is faithful, these are real workflow bugs to fix BEFORE Nadim.");

        return conformanceFailures == 0 ? 0 : 2;
    }

    private static int FindCol(IXLWorksheet ws, string header, int lastCol)
    {
        for (var c = 1; c <= lastCol; c++)
        {
            if (string.Equals(ws.Cell(1, c).GetString()?.Trim(), header, StringComparison.OrdinalIgnoreCase))
                return c;
        }
        return -1;
    }

    private static string Trunc(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
