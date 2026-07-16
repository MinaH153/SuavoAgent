namespace PioneerRxSim;

/// <summary>
/// One supplier row in the simulated Pricing-tab catalog grid. Costs are decimals here;
/// the sim renders them per-variant (plain F4 or "$"-prefixed for the currency probe).
/// </summary>
public sealed record SimSupplierRow(
    string Supplier,
    string ItemNumber,
    string PackageSize,
    decimal Cost,
    decimal CostPerUnit,
    string Status,
    string LastPurchase,
    /// <summary>Non-null = the grid exposes this TRUNCATED string as the cell's UIA Name
    /// (DevExpress-style render truncation) while ValuePattern still carries the full
    /// Supplier. Probes GetCellText's ValuePattern-over-Name preference.</summary>
    string? TruncatedAutomationName = null,
    bool Linked = true,
    string InventoryGroup = "Rx",
    bool Discontinued = false);

public sealed record SimItem(
    string Ndc11,
    string DisplayName,
    IReadOnlyList<SimSupplierRow> Suppliers);

public enum ExpectationKind
{
    /// <summary>A definite contract: mismatch = rehearsal conformance FAILURE (exit 2).</summary>
    MustMatch,
    /// <summary>A probe whose outcome is itself the finding (known workflow gap or open
    /// question). Never fails the run; the evaluator prints a graded FINDING either way.</summary>
    Probe,
}

/// <summary>What the priced output workbook must contain for one NDC row.</summary>
public sealed record NdcExpectation(
    string Ndc11,
    ExpectationKind Kind,
    /// <summary>Exact expected "Price Lookup Status" cell ("OK", "NO_MATCH", "ERROR: ...").
    /// Compared with StartsWith when it ends with '…', else exact (ordinal).</summary>
    string ExpectedStatus,
    string? ExpectedSupplier,
    decimal? ExpectedCost,
    string Proves,
    /// <summary>For Probe rows: what an "OK"/wrong outcome means (the pre-written finding).</summary>
    string? GapNote = null);

/// <summary>
/// Single source of truth for the seeded item database AND the rehearsal's expected outcomes.
/// The driver project references this so the sim and the assertions can never drift.
/// Expected values are HAND-WRITTEN (not derived through PricingGridReader) so the rehearsal
/// is an independent oracle, not the code grading itself.
/// </summary>
public static class SimCatalog
{
    // ── NDCs (11-digit canonical) ────────────────────────────────────────────
    public const string NdcMultiSupplier = "00006073460"; // raw in workbook: 0006-0734-60 (4-4-2)
    public const string NdcSingleSupplier = "50242004121"; // raw: 50242-041-21 (5-3-2)
    public const string NdcNoMatch = "99999999999"; // raw: 99999-9999-99 — NOT seeded
    public const string NdcWinnerLast = "68645055290"; // raw: 68645-0552-90 (5-4-2)
    public const string NdcDoNotUse = "12345678901"; // raw: 12345-6789-01
    public const string NdcAllDiscontinued = "00071015523"; // raw: 00071015523 (11-digit)
    public const string NdcTwoSupplier = "00093505698"; // raw: 00093505698
    public const string NdcPrecision = "55111046530"; // raw: 55111-0465-30

    /// <summary>Raw NDC cell values for the rehearsal workbook, in row order. The no-match row
    /// deliberately FOLLOWS a happy row so the persisted-last-item stale-grid probe is armed.
    /// The final row is an unparseable NDC (reader-level Invalid path).</summary>
    public static readonly IReadOnlyList<(string Raw, string DrugName)> WorkbookRows = new[]
    {
        ("0006-0734-60", "PROAIR HFA 90MCG INHALER"),
        ("50242-041-21", "XOLAIR 150MG VIAL"),
        ("99999-9999-99", "PHANTOMYCIN 500MG (not in PMS)"),
        ("68645-0552-90", "LISINOPRIL 10MG TAB"),
        ("12345-6789-01", "OMEPRAZOLE DR 20MG CAP"),
        ("00071015523", "ATORVASTATIN 80MG TAB"),
        ("00093505698", "METFORMIN ER 500MG TAB"),
        ("55111-0465-30", "PANTOPRAZOLE 40MG TAB"),
        ("1234-ABC", "BROKEN ROW (unparseable NDC)"),
    };

    public const string TruncationWinnerFullName = "Mckesson Geriatric Specialty Whsle LLC";
    public const string TruncationWinnerTruncated = "Mckesson Geri…";
    public const string VirtualDepthWinner = "Verity Wholesale Direct";
    public const decimal VirtualDepthWinnerCost = 0.2999m;

    /// <summary>Item database the sim serves. Variant only changes the multi-supplier NDC
    /// (VirtualDepth swaps in the 40-row set); everything else is variant-independent.</summary>
    public static IReadOnlyDictionary<string, SimItem> Items(SimVariant variant)
    {
        var multi = variant == SimVariant.VirtualDepth
            ? VirtualDepthSuppliers()
            : new[]
            {
                // Grid is sorted by Supplier ASC — the usable minimum (0.3719, Mckesson Geri…)
                // sits mid-list, and a CHEAPER row (Anda, 0.3675) is Discontinued and must be
                // excluded. Row 1 by sort (AmerisourceBergen) is NOT the answer.
                new SimSupplierRow("AmerisourceBergen", "10026401", "8.5GM", 41.12m, 0.4112m, "Active", "2026-05-18"),
                new SimSupplierRow("Anda Inc", "20077134", "8.5GM", 36.75m, 0.3675m, "Discontinued", "2025-11-02"),
                new SimSupplierRow("Cardinal Health", "30019920", "8.5GM", 39.98m, 0.3998m, "Active", "2026-06-01"),
                new SimSupplierRow(TruncationWinnerFullName, "40026655", "8.5GM", 37.19m, 0.3719m, "Active", "2026-05-30",
                    TruncatedAutomationName: TruncationWinnerTruncated),
                new SimSupplierRow("McKesson Connect", "40026702", "8.5GM", 38.50m, 0.3850m, "Active", "2026-05-25"),
                new SimSupplierRow("Real Value Rx", "50031177", "8.5GM", 42.50m, 0.4250m, "Active", "2026-04-12"),
            };

        var items = new[]
        {
            new SimItem(NdcMultiSupplier, "PROAIR HFA 90MCG INHALER", multi),
            new SimItem(NdcSingleSupplier, "XOLAIR 150MG VIAL", new[]
            {
                new SimSupplierRow("Genentech Direct", "61500021", "1 VIAL", 612.50m, 612.5000m, "Active", "2026-05-29"),
            }),
            new SimItem(NdcWinnerLast, "LISINOPRIL 10MG TAB", new[]
            {
                // Sorted by Supplier ASC the winner (Zydus, 0.0312) is the LAST visual row.
                new SimSupplierRow("Cardinal Health", "30055210", "1000 CT", 41.20m, 0.0412m, "Active", "2026-06-02"),
                new SimSupplierRow("KeySource Medical", "70012006", "1000 CT", 38.80m, 0.0388m, "Active", "2026-05-21"),
                new SimSupplierRow("NorthStar RxLLC", "80077120", "1000 CT", 37.90m, 0.0379m, "Active", "2026-05-11"),
                new SimSupplierRow("Zydus Direct", "90010065", "1000 CT", 31.20m, 0.0312m, "Active", "2026-06-05"),
            }),
            new SimItem(NdcDoNotUse, "OMEPRAZOLE DR 20MG CAP (Do Not Use)", new[]
            {
                // Plausible rows on purpose: if the workflow prices this item the output LOOKS
                // legitimate — exactly the silent data-integrity failure the probe demonstrates.
                new SimSupplierRow("AmerisourceBergen", "10027815", "500 CT", 12.40m, 0.0248m, "Active", "2026-03-30"),
                new SimSupplierRow("Cardinal Health", "30027816", "500 CT", 11.90m, 0.0238m, "Active", "2026-04-02"),
            }),
            new SimItem(NdcAllDiscontinued, "ATORVASTATIN 80MG TAB", new[]
            {
                new SimSupplierRow("Anda Inc", "20088101", "500 CT", 22.10m, 0.0442m, "Discontinued", "2025-09-14"),
                new SimSupplierRow("Cardinal Health", "30088102", "500 CT", 21.05m, 0.0421m, "Unavailable", "2025-12-01"),
                new SimSupplierRow("McKesson Connect", "40088103", "500 CT", 23.90m, 0.0478m, "Inactive", "2025-10-20"),
            }),
            new SimItem(NdcTwoSupplier, "METFORMIN ER 500MG TAB", new[]
            {
                // Package-cost adversaries mirror Nadim's live grid. The package winner
                // (Real Value Rx, $3.16) intentionally disagrees with the CPU winner
                // (McKesson, $4.95 / 500 = $0.0099 per unit).
                new SimSupplierRow("Unlinked Bargain", "10099200", "100 CT", 1.00m, 0.1000m, "Available", "2026-06-01", Linked: false),
                new SimSupplierRow("340B Bargain", "10099201", "100 CT", 1.50m, 0.1000m, "Available", "2026-06-01", InventoryGroup: "340B"),
                // Adversarial live mismatch: Status still says Available, so
                // row-level status checks alone cannot identify it. Only the
                // fixed Include Discontinued=No filter removes this cheapest row.
                new SimSupplierRow("Discontinued Available Bargain", "10099199", "100 CT", 0.50m, 0.0050m, "Available", "2025-08-01", Discontinued: true),
                new SimSupplierRow("Discontinued Bargain", "10099202", "100 CT", 2.00m, 0.0050m, "Discontinued", "2025-09-01"),
                new SimSupplierRow("Real Value Rx", "755511106", "100 CT", 3.16m, 0.0316m, "Available", "2026-06-03"),
                new SimSupplierRow("McKesson", "1583772", "500 CT", 4.95m, 0.0099m, "Available", "2026-06-04"),
            }),
            new SimItem(NdcPrecision, "PANTOPRAZOLE 40MG TAB", new[]
            {
                // 0.0795 vs 0.08 — sub-cent precision must survive the text round-trip.
                new SimSupplierRow("Aurobindo Direct", "20011230", "90 CT", 7.20m, 0.0800m, "Active", "2026-05-07"),
                new SimSupplierRow("Camber Direct", "30011231", "90 CT", 7.155m, 0.0795m, "Active", "2026-05-19"),
            }),
        };

        return items.ToDictionary(i => i.Ndc11, i => i);
    }

    private static SimSupplierRow[] VirtualDepthSuppliers()
    {
        // 40 rows; sorted-by-supplier the winner ("Verity Wholesale Direct", 0.2999) lands at
        // index ~33 — far below the ~8 realized rows of a 240px-tall virtualized grid.
        var rows = new List<SimSupplierRow>();
        for (var i = 0; i < 39; i++)
        {
            // "Supplier 01" … "Supplier 39" sort before "Verity…"; costs descend 0.48 → 0.32
            // so the realized top-of-grid never contains the true minimum.
            var cost = 0.4800m - (i * 0.0040m);
            rows.Add(new SimSupplierRow(
                $"Supplier {i + 1:00} Wholesale", $"99{i:000}11", "8.5GM",
                Math.Round(cost * 100m, 2), Math.Round(cost, 4), "Active", "2026-05-01"));
        }
        rows.Add(new SimSupplierRow(VirtualDepthWinner, "9994011", "8.5GM",
            29.99m, VirtualDepthWinnerCost, "Active", "2026-06-04"));
        return rows.ToArray();
    }

    // ── Expected outcomes per variant ───────────────────────────────────────
    // Status strings ending in '…' are StartsWith-prefix expectations.

    private const string SchemaError =
        "ERROR: Pricing grid schema not recognized — Supplier/Cost columns missing or renamed";
    private const string NoRowsError = "ERROR: Pricing grid has no rows";
    private const string NoUsableError = "ERROR: No usable supplier rows in Pricing tab";
    private const string MenuError = "ERROR: Could not open Item → Rx Item menu";

    public static IReadOnlyList<NdcExpectation> ExpectedFor(SimVariant variant, bool clearSearchAfterLoad)
    {
        var invalidRow = new NdcExpectation(
            "1234-ABC", ExpectationKind.MustMatch, "ERROR: Invalid NDC:…", null, null,
            "ExcelPricingReader rejects unparseable NDCs before any UIA work; row gets an explicit marker.");

        if (variant == SimVariant.WpfMenu)
        {
            // Every UIA row dies at step 1: stock WPF Menu is ControlType.Menu, the workflow
            // asserts ControlType.MenuBar. THE control-type finding.
            var all = new List<NdcExpectation>();
            foreach (var ndc in new[] { NdcMultiSupplier, NdcSingleSupplier, NdcNoMatch, NdcWinnerLast,
                NdcDoNotUse, NdcAllDiscontinued, NdcTwoSupplier, NdcPrecision })
            {
                all.Add(new NdcExpectation(ndc, ExpectationKind.MustMatch, MenuError, null, null,
                    "WPF-native Menu (ControlType.Menu) is invisible to FindFirstDescendant(ControlType.MenuBar). " +
                    "If PioneerRx's menu is WPF-native the entire workflow is dead at step 1 — fix is a Menu-or-MenuBar tolerant lookup."));
            }
            all.Add(invalidRow);
            return all;
        }

        string happyStatus = variant switch
        {
            SimVariant.RenamedCost => SchemaError,
            SimVariant.GlacialGrid => NoRowsError,
            SimVariant.CurrencyCells => NoUsableError,
            _ => "OK",
        };

        NdcExpectation Happy(string ndc, string supplier, decimal cost, string proves) =>
            happyStatus == "OK"
                ? new NdcExpectation(ndc, ExpectationKind.MustMatch, "OK", supplier, cost, proves)
                : new NdcExpectation(ndc, ExpectationKind.MustMatch, happyStatus, null, null, VariantProof(variant));

        var multi = variant == SimVariant.VirtualDepth
            ? new NdcExpectation(NdcMultiSupplier, ExpectationKind.MustMatch, "OK",
                VirtualDepthWinner, VirtualDepthWinnerCost,
                "Virtualization: the true minimum lives ~25 rows below the realized window. " +
                "PASS = UIA2 FindAllChildren sees unrealized rows WITH readable cells. " +
                "FAIL (a realized-subset supplier/cost returned) = virtualization blindness; the workflow needs " +
                "scroll-and-merge or ItemContainerPattern/VirtualizedItemPattern (UIA3-flavored) before a long DevExpress grid can be trusted.")
            : Happy(NdcMultiSupplier, TruncationWinnerFullName, 0.3719m,
                "min(Cost Per Unit) across ALL usable rows (winner mid-grid, not row 1); cheaper-but-Discontinued row excluded; " +
                "FULL supplier name written back via ValuePattern despite truncated cell Name.");

        var list = new List<NdcExpectation>
        {
            multi,
            Happy(NdcSingleSupplier, "Genentech Direct", 612.5000m, "Single-supplier grid; high-value decimal survives round-trip."),

            new NdcExpectation(NdcNoMatch, ExpectationKind.Probe,
                happyStatus == "OK" ? "NO_MATCH" : happyStatus, null, null,
                "No-match NDC typed AFTER a successful row, with the Edit window persisting the last item.",
                GapNote: "VerifyLoadedNdc scans Edit controls FIRST and the Quick Search box still contains the " +
                         "typed NDC → verification is tautological for ANY typed NDC. With a persisted previous item, " +
                         "the workflow then reads the STALE grid and returns the previous item's pricing as status OK " +
                         "for an NDC that does not exist — Precedence-1 wrong-data. CORRECT behavior = NO_MATCH. " +
                         "Workflow fix candidate: exclude the search box element (or all focusable Edits) from the verify scan."),

            Happy(NdcWinnerLast, "Zydus Direct", 0.0312m, "Winner at the LAST visual row (sort is supplier-asc, not cost-asc) — never trust row 1."),

            clearSearchAfterLoad && happyStatus == "OK"
                ? new NdcExpectation(NdcDoNotUse, ExpectationKind.MustMatch, "NO_MATCH", null, null,
                    "With the search box cleared after load, VerifyLoadedNdc reaches the item-name Text element, " +
                    "sees '(Do Not Use)' and refuses to price (LooksLikeDoNotUse).")
                : new NdcExpectation(NdcDoNotUse, ExpectationKind.Probe,
                    happyStatus == "OK" ? "NO_MATCH" : happyStatus, null, null,
                    "Red '(Do Not Use)' duplicate item must never be priced.",
                    GapNote: "With the typed NDC still in the Quick Search box, VerifyLoadedNdc matches the BOX " +
                             "(scanned before any Text element) and returns true — the Do-Not-Use guard is unreachable " +
                             "and the DNU item gets priced with plausible-looking numbers (status OK). " +
                             "Same root cause as the no-match tautology; same fix."),

            happyStatus == "OK"
                ? new NdcExpectation(NdcAllDiscontinued, ExpectationKind.MustMatch, NoUsableError, null, null,
                    "All rows Discontinued/Unavailable/Inactive → SelectCheapest returns null → explicit per-row error, no price. " +
                    "(Mini-finding: writer's MarkerFor misses this phrasing — lands as ERROR: text, not NO_SUPPLIER_ROWS.)")
                : new NdcExpectation(NdcAllDiscontinued, ExpectationKind.MustMatch,
                    variant == SimVariant.CurrencyCells ? NoUsableError : happyStatus, null, null, VariantProof(variant)),

            Happy(
                NdcTwoSupplier,
                "McKesson",
                0.0099m,
                "CPU winner differs from the eligible package-cost winner (Real Value Rx at $3.16)."),
            Happy(NdcPrecision, "Camber Direct", 0.0795m, "Sub-cent precision (0.0795 beats 0.0800) survives text→decimal."),
            invalidRow,
        };

        if (variant == SimVariant.SlowGrid)
        {
            // Every happy row becomes a probe: most winners live in batch 2, and a 1.2s
            // inter-batch gap exceeds the ~500ms stable-row quiet window (2 consecutive equal
            // 250ms reads) — WaitForStableRows can declare 'loaded' on the partial set and
            // return a batch-1 minimum with status OK. The observed outcome IS the finding.
            list = list.Select(e => e is { Kind: ExpectationKind.MustMatch, ExpectedStatus: "OK" }
                ? e with
                {
                    Kind = ExpectationKind.Probe,
                    GapNote = "SlowGrid: if this row reports a batch-1 supplier (or a partial-set minimum) with " +
                              "status OK, the stable-row quiet window is too short for slow grids. " +
                              "Workflow fix candidate: longer quiet window or a row-count cross-check. " +
                              $"True minimum: {e.ExpectedSupplier} @ {e.ExpectedCost}.",
                }
                : e).ToList();
        }

        return list;
    }

    private static string VariantProof(SimVariant variant) => variant switch
    {
        SimVariant.RenamedCost =>
            "Renamed cost header must fail CLOSED by header-name resolution — explicit schema error per row, " +
            "no ordinal fallback, no wrong-column data written.",
        SimVariant.GlacialGrid =>
            "Rows arriving after GridLoadTimeout must fail the row closed (no rows ≠ a price).",
        SimVariant.CurrencyCells =>
            "'$'-prefixed costs are unparseable under InvariantCulture TryParseCost → every row unusable. " +
            "If the REAL grid renders currency symbols this is a 100% failure mode — verify on Nadim's box; " +
            "fix candidate: strip currency symbols before parse.",
        _ => "",
    };
}
