using ClosedXML.Excel;
using PioneerRxSim;

namespace PioneerRxRehearsal;

/// <summary>
/// Generates the Nadim-shaped input workbook: a drug list with an NDC column, exactly what
/// ExcelPricingReader expects ("NDC" header, one NDC per row, mixed real-world NDC shapes).
/// </summary>
public static class RehearsalWorkbook
{
    /// <summary>
    /// Quick-mode workbook: the 9 catalog rows (happy / no-match / Do-Not-Use / all-discontinued /
    /// unparseable). Row order is load-bearing: the no-match NDC follows a happy row so the
    /// persisted-last-item stale-grid probe is armed.
    /// </summary>
    public static void WriteQuick(string path) => Write(path, SimCatalog.WorkbookRows);

    /// <summary>
    /// Full-mode workbook for the REAL file-discovery chain. PharmacyPresets.NdcPricingList()
    /// expects 50–2000 rows (TabularExpectation MinRows=50), so the 9 probe rows are padded
    /// with happy-NDC repeats to 55 rows. Same expectations apply per-NDC.
    /// </summary>
    public static void WriteFull(string path)
    {
        var rows = new List<(string Raw, string DrugName)>(SimCatalog.WorkbookRows);
        var pads = new[]
        {
            ("0006-0734-60", "PROAIR HFA 90MCG INHALER"),
            ("68645-0552-90", "LISINOPRIL 10MG TAB"),
            ("00093505698", "METFORMIN ER 500MG TAB"),
            ("55111-0465-30", "PANTOPRAZOLE 40MG TAB"),
        };
        var i = 0;
        while (rows.Count < 55)
        {
            var (raw, name) = pads[i % pads.Length];
            rows.Add((raw, $"{name} (refill list #{i + 2})"));
            i++;
        }
        Write(path, rows);
    }

    private static void Write(string path, IReadOnlyList<(string Raw, string DrugName)> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Top Generics");
        ws.Cell(1, 1).Value = "Drug Name";
        ws.Cell(1, 2).Value = "NDC";
        ws.Cell(1, 3).Value = "Qty Dispensed (90d)";

        var rnd = new Random(20260611); // deterministic
        for (var r = 0; r < rows.Count; r++)
        {
            ws.Cell(r + 2, 1).Value = rows[r].DrugName;
            ws.Cell(r + 2, 2).Value = rows[r].Raw;
            ws.Cell(r + 2, 3).Value = rnd.Next(40, 900);
        }
        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }
}
