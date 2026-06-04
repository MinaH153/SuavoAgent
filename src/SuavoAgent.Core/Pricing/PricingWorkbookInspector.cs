using ClosedXML.Excel;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// PHI-safe description of a pricing workbook's columns — header name + per-column shape — so the
/// baseline-cost and quantity columns can be IDENTIFIED remotely (from agent telemetry) and then
/// wired via the cloud config-override, without anyone shipping the workbook off the box.
///
/// <para><b>PHI-safe by construction.</b> It reports only (a) the header text (column titles like
/// "NDC" / "Current Cost" / "Qty" — not patient data) and (b) for numeric columns, the count + min +
/// max. It NEVER reports a text cell's value, so a stray "Patient Name" column leaks only its title,
/// never its contents.</para>
/// </summary>
public static class PricingWorkbookInspector
{
    public sealed record ColumnInfo(int Column, string Header, string Kind, int NumericCount, decimal? Min, decimal? Max)
    {
        public override string ToString() => Kind == "numeric"
            ? $"[{Column}] \"{Header}\": numeric n={NumericCount} min={Min} max={Max}"
            : $"[{Column}] \"{Header}\": {Kind}";
    }

    /// <summary>
    /// Describe each column of the first worksheet. Scans at most <paramref name="maxRowsScanned"/>
    /// data rows for the numeric stats. Returns empty if the file is missing/unreadable.
    /// </summary>
    public static IReadOnlyList<ColumnInfo> Describe(string path, int maxRowsScanned = 500)
    {
        if (!File.Exists(path)) return Array.Empty<ColumnInfo>();
        try
        {
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheets.FirstOrDefault();
            if (ws is null) return Array.Empty<ColumnInfo>();

            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastCol < 1) return Array.Empty<ColumnInfo>();

            var scanTo = Math.Min(lastRow, 1 + Math.Max(0, maxRowsScanned));
            var cols = new List<ColumnInfo>(lastCol);
            for (int c = 1; c <= lastCol; c++)
            {
                var header = ws.Cell(1, c).GetString()?.Trim() ?? "";
                int numericCount = 0;
                bool anyText = false;
                decimal? min = null, max = null;
                for (int r = 2; r <= scanTo; r++)
                {
                    var cell = ws.Cell(r, c);
                    if (cell.IsEmpty()) continue;
                    if (cell.TryGetValue(out decimal d))
                    {
                        numericCount++;
                        if (min is null || d < min) min = d;
                        if (max is null || d > max) max = d;
                    }
                    else
                    {
                        anyText = true; // value deliberately NOT recorded (PHI-safe)
                    }
                }

                var kind = numericCount > 0 ? "numeric" : anyText ? "text" : "empty";
                cols.Add(new ColumnInfo(c, header, kind, numericCount, min, max));
            }
            return cols;
        }
        catch
        {
            return Array.Empty<ColumnInfo>();
        }
    }

    /// <summary>One-line PHI-safe summary for a log/telemetry line.</summary>
    public static string Summarize(IReadOnlyList<ColumnInfo> columns) =>
        columns.Count == 0 ? "(no columns)" : string.Join(" | ", columns.Select(c => c.ToString()));
}
