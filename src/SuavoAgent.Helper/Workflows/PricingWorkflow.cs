using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Executes the PioneerRx pricing lookup workflow via UIA:
///   Item menu → Rx Item → Quick Search (NDC) → Pricing tab → read supplier grid
///
/// Navigation path confirmed from field screenshots (Apr 4, 2026):
///   - Top menu: Item → Rx Item opens "Edit Rx Item" window
///   - Quick Search field at top accepts NDC
///   - Pricing tab shows supplier catalog with Cost, Cost Per Unit columns
///   - Cheapest = row with lowest Cost Per Unit (sorted ascending by default)
/// </summary>
public sealed class PricingWorkflow
{
    private readonly PioneerRxUiaEngine _engine;
    private readonly ILogger _logger;

    // UIA element identifiers confirmed from screenshots
    private const string ItemMenuName = "Item";
    private const string RxItemMenuName = "Rx Item";
    private const string QuickSearchHint = "Quick Search";
    private const string PricingTabName = "Pricing";
    private const string EditRxItemWindowTitle = "Edit Rx Item";

    // How long to wait for UI elements to appear after navigation
    private static readonly TimeSpan ElementTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan GridLoadTimeout = TimeSpan.FromSeconds(5);

    public PricingWorkflow(PioneerRxUiaEngine engine, ILogger logger)
    {
        _engine = engine;
        _logger = logger;
    }

    /// <summary>
    /// Looks up pricing for a single NDC. Returns the cheapest supplier row.
    /// Leaves PioneerRx in a usable state (closes Edit Rx Item dialog after read).
    /// </summary>
    public SupplierPriceResult Lookup(NdcPricingRequest request)
    {
        // M2a capture: GREEN-tier selector-resolution telemetry per step. Builtin source only
        // (no learned patches yet). Candidate scans run ONLY on the rare failure path, so the
        // happy path is unchanged. Never records element Name/Value/Text — PHI-negative by type.
        var observations = new List<SelectorObservation>();
        void Observe(SelectorStepId step, SelectorOutcome outcome, SelectorFailureKind kind,
                     IReadOnlyList<ObservedElement>? candidates = null) =>
            observations.Add(new SelectorObservation(
                step, SelectorResolvedVia.Builtin, outcome, kind, null,
                candidates ?? Array.Empty<ObservedElement>()));
        SupplierPriceResult Done(SupplierPriceResult r) => r with { Observations = observations };

        // M2b: the resolver tries any active learned selector patch before the hardcoded builtin.
        // With no patches (the case until M2c distributes one) it IS the builtin find — today's
        // behavior unchanged. The builtin always backstops a missed learned selector per element.
        var resolver = new SelectorResolver(request.Patches);

        var mainWindow = _engine.MainWindow;
        if (mainWindow == null)
            return Done(Fail(request, "PioneerRx main window not available"));

        try
        {
            using var automation = new UIA2Automation();
            var cf = automation.ConditionFactory;

            // Step 1: Open Item → Rx Item from the menu bar
            if (!OpenRxItemDialog(mainWindow, cf, resolver))
            {
                Observe(SelectorStepId.OpenRxItem, SelectorOutcome.Failed, SelectorFailureKind.ElementNotFound,
                    ScanCandidates(mainWindow, cf, ControlType.MenuItem));
                return Done(Fail(request, "Could not open Item → Rx Item menu"));
            }
            Observe(SelectorStepId.OpenRxItem, SelectorOutcome.Resolved, SelectorFailureKind.None);

            // Step 2: Find the Edit Rx Item window
            var editWindow = WaitForWindow(automation, EditRxItemWindowTitle);
            if (editWindow == null)
            {
                Observe(SelectorStepId.QuickSearchField, SelectorOutcome.Failed, SelectorFailureKind.Timeout);
                return Done(Fail(request, "Edit Rx Item window did not appear"));
            }

            try
            {
                // Step 3: Type NDC into Quick Search and press Enter
                if (!SearchByNdc(editWindow, cf, request.Ndc, resolver))
                {
                    Observe(SelectorStepId.QuickSearchField, SelectorOutcome.Failed, SelectorFailureKind.ElementNotFound,
                        ScanCandidates(editWindow, cf, ControlType.Edit));
                    return Done(Fail(request, $"Could not enter NDC {request.Ndc} in Quick Search"));
                }
                Observe(SelectorStepId.QuickSearchField, SelectorOutcome.Resolved, SelectorFailureKind.None);

                // [C-3] Verify the loaded item's NDC matches the requested NDC before reading pricing.
                // Prevents returning pricing data for the previously-selected item when Quick Search
                // is slow or finds no match. (Selector resolved; the mismatch is semantic, so no
                // candidate scan — and we must not read element values here.)
                if (!VerifyLoadedNdc(editWindow, cf, request.Ndc))
                {
                    Observe(SelectorStepId.VerifyNdc, SelectorOutcome.Failed, SelectorFailureKind.VerifyMismatch);
                    return Done(Fail(request, $"Loaded item NDC does not match {request.Ndc} — item may not exist or search timed out"));
                }
                Observe(SelectorStepId.VerifyNdc, SelectorOutcome.Resolved, SelectorFailureKind.None);

                // Step 4: Navigate to Pricing tab
                if (!ClickPricingTab(editWindow, cf, resolver))
                {
                    Observe(SelectorStepId.PricingTab, SelectorOutcome.Failed, SelectorFailureKind.ElementNotFound,
                        ScanCandidates(editWindow, cf, ControlType.TabItem));
                    return Done(Fail(request, "Could not click Pricing tab"));
                }
                Observe(SelectorStepId.PricingTab, SelectorOutcome.Resolved, SelectorFailureKind.None);

                // Step 5: Read the supplier grid — find cheapest (lowest cost per unit)
                var cheapest = ReadCheapestSupplier(editWindow, cf, out var gridFailure);
                if (cheapest == null)
                {
                    Observe(SelectorStepId.SupplierGrid, SelectorOutcome.Failed, SelectorFailureKind.GridEmpty,
                        ScanCandidates(editWindow, cf, ControlType.Table)
                            .Concat(ScanCandidates(editWindow, cf, ControlType.DataGrid)).Take(5).ToList());
                    return Done(new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                        false, null, null, gridFailure ?? "No supplier rows found in Pricing tab"));
                }
                Observe(SelectorStepId.SupplierGrid, SelectorOutcome.Resolved, SelectorFailureKind.None);

                _logger.Debug("PricingWorkflow: NDC {Ndc} → {Supplier} @ {Cost}/unit",
                    request.Ndc, cheapest.Value.supplier, cheapest.Value.cost);

                return Done(new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                    true, cheapest.Value.supplier, cheapest.Value.cost, null));
            }
            finally
            {
                // Always close the Edit Rx Item dialog — press Escape
                TryCloseEditWindow(editWindow, cf);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "PricingWorkflow: unhandled error for NDC {Ndc}", request.Ndc);
            // Compliance: never upload a raw exception message (uncontrolled free text — possible
            // leak vector). The real exception is in the local log above; the corpus/cloud get a
            // generic reason + the structured observations only.
            return Done(Fail(request, "Pricing lookup failed (unhandled error)"));
        }
    }

    // GREEN-tier candidate scan for the failure path: records ControlType / AutomationId /
    // ClassName of up to 5 elements of the given type so the fleet corpus can learn what was
    // actually on screen when a builtin selector missed. NEVER reads Name/Value/Text (PHI tier).
    private static IReadOnlyList<ObservedElement> ScanCandidates(
        AutomationElement root, ConditionFactory cf, ControlType type)
    {
        try
        {
            return root.FindAllDescendants(cf.ByControlType(type))
                .Take(5)
                .Select(ToObserved)
                .ToList();
        }
        catch
        {
            return Array.Empty<ObservedElement>();
        }
    }

    private static ObservedElement ToObserved(AutomationElement el)
    {
        string controlType;
        try { controlType = el.ControlType.ToString(); } catch { controlType = "Unknown"; }
        string? automationId;
        try { automationId = NullIfEmpty(el.AutomationId); } catch { automationId = null; }
        string? className;
        try { className = NullIfEmpty(el.ClassName); } catch { className = null; }
        return new ObservedElement(controlType, automationId, className);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // Visibility into learned-patch usage during a run. The FallbackUsed line is the early
    // signal that a learned selector has drifted (input to later auto-retirement / M2c-M2d).
    private void LogIfLearned(SelectorStepId step, SelectorResolver.Resolution res)
    {
        if (res.ResolvedVia == SelectorResolvedVia.Learned)
            _logger.Information("PricingWorkflow: step {Step} resolved via learned patch {PatchId}", step, res.PatchId);
        else if (res.Outcome == SelectorOutcome.FallbackUsed)
            _logger.Warning("PricingWorkflow: step {Step} learned patch {PatchId} missed — used builtin fallback", step, res.PatchId);
    }

    private bool OpenRxItemDialog(Window mainWindow, ConditionFactory cf, SelectorResolver resolver)
    {
        try
        {
            // Click "Item" in the menu bar
            var menuBar = mainWindow.FindFirstDescendant(cf.ByControlType(ControlType.MenuBar));
            if (menuBar == null) return false;

            var (itemMenu, itemRes) = resolver.FindFirst(menuBar, cf, SelectorStepId.OpenItemMenu, cf.ByName(ItemMenuName));
            if (itemMenu == null) return false;
            LogIfLearned(SelectorStepId.OpenItemMenu, itemRes);

            itemMenu.AsMenuItem()?.Click();
            Thread.Sleep(300);

            // Click "Rx Item" in the dropdown
            var (rxItemEntry, rxRes) = resolver.FindFirst(mainWindow, cf, SelectorStepId.OpenRxItem, cf.ByName(RxItemMenuName));
            if (rxItemEntry == null) return false;
            LogIfLearned(SelectorStepId.OpenRxItem, rxRes);

            rxItemEntry.AsMenuItem()?.Click();
            Thread.Sleep(300);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: OpenRxItemDialog failed");
            return false;
        }
    }

    private Window? WaitForWindow(UIA2Automation automation, string title)
    {
        var deadline = DateTime.UtcNow + ElementTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var desktop = automation.GetDesktop();
                var cf = automation.ConditionFactory;
                var win = desktop.FindFirstDescendant(cf.ByName(title))?.AsWindow();
                if (win != null) return win;
            }
            catch { }
            Thread.Sleep(200);
        }
        return null;
    }

    // SearchByNdc was Codex-flagged as brittle for 500-row batches:
    // the original sequence was Focus -> Sleep(100) -> Ctrl+A -> Sleep(50) ->
    // Type(ndc) -> Sleep(100) -> Enter -> Sleep(800) with no verification.
    // Window focus changes mid-row (toast popups, antivirus dialogs, or just
    // OS desktop redraws) would silently drop keystrokes or fire Enter against
    // the wrong control. Now we:
    //   (1) find the search box,
    //   (2) attempt the type-and-press sequence up to MaxTypeAttempts times,
    //       verifying after each that the box actually contains the NDC we
    //       intended to send before pressing Enter,
    //   (3) bail with false (caller writes a failure result for the row) if
    //       all attempts fail rather than firing Enter against an unknown state.
    private const int MaxTypeAttempts = 2;

    private bool SearchByNdc(Window editWindow, ConditionFactory cf, string ndc, SelectorResolver resolver)
    {
        try
        {
            // Quick Search is a text box near the top of the Edit Rx Item window
            var deadline = DateTime.UtcNow + ElementTimeout;
            AutomationElement? searchBox = null;
            while (DateTime.UtcNow < deadline)
            {
                // Learned patch first, then the builtin (ControlType=Edit + HelpText "Quick Search").
                var builtin = new FlaUI.Core.Conditions.AndCondition(
                    cf.ByControlType(ControlType.Edit),
                    cf.ByHelpText(QuickSearchHint));
                var (box, res) = resolver.FindFirst(editWindow, cf, SelectorStepId.QuickSearchField, builtin);
                searchBox = box;
                if (searchBox != null) LogIfLearned(SelectorStepId.QuickSearchField, res);

                if (searchBox == null)
                {
                    // Fallback: first Edit control at the top of the window
                    var edits = editWindow.FindAllDescendants(cf.ByControlType(ControlType.Edit));
                    searchBox = edits.FirstOrDefault();
                }

                if (searchBox != null) break;
                Thread.Sleep(200);
            }

            if (searchBox == null) return false;

            for (int attempt = 1; attempt <= MaxTypeAttempts; attempt++)
            {
                searchBox.Focus();
                Thread.Sleep(100);

                // Clear existing text then type NDC
                Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                    FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
                Thread.Sleep(50);
                Keyboard.Type(ndc);
                Thread.Sleep(150);

                // Before firing Enter, confirm the search box actually contains
                // the NDC we just typed. A common failure mode is that the OS
                // shifted focus between Focus() and Type() — Ctrl+A was a no-op
                // and Type() landed in some other window. Polling the textbox
                // value here catches that without nuking the user's foreground.
                if (SearchBoxContainsNdc(searchBox, ndc))
                {
                    Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
                    Thread.Sleep(800);
                    return true;
                }

                _logger.Warning(
                    "PricingWorkflow: SearchByNdc attempt {Attempt}/{Max} — search box does not contain {Ndc} after type; retrying",
                    attempt, MaxTypeAttempts, ndc);
                // Brief backoff before next attempt — gives a transient focus
                // thief (notification, modal) time to clear.
                Thread.Sleep(300);
            }

            _logger.Warning(
                "PricingWorkflow: SearchByNdc giving up on {Ndc} after {Max} attempts — never confirmed text landed in Quick Search",
                ndc, MaxTypeAttempts);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: SearchByNdc failed for {Ndc}", ndc);
            return false;
        }
    }

    private static bool SearchBoxContainsNdc(AutomationElement searchBox, string ndc)
    {
        try
        {
            var text = searchBox.AsTextBox()?.Text ?? searchBox.Name ?? "";
            if (string.IsNullOrEmpty(text)) return false;
            // Match permissively — PioneerRx may apply input masks (5-4-2) or
            // strip non-digits. Compare digit-only forms.
            var typedDigits = new string(ndc.Where(char.IsDigit).ToArray());
            var observedDigits = new string(text.Where(char.IsDigit).ToArray());
            return observedDigits.Contains(typedDigits, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies that the Edit Rx Item window contains the expected NDC after Quick Search loads.
    /// Scans all text-bearing elements for the normalized NDC (hyphens removed, 11 digits).
    /// Returns false if the NDC is not found within the element timeout, indicating the wrong
    /// item was loaded or no result was returned.
    /// </summary>
    private bool VerifyLoadedNdc(Window editWindow, ConditionFactory cf, string ndc)
    {
        // Caller already normalized to 11-digit canonical form upstream (ExcelPricingReader).
        // If this invariant breaks we'd silently match shorter substrings, so assert + fall back.
        var normalizedNdc = NdcNormalizer.TryNormalize(ndc);
        if (string.IsNullOrEmpty(normalizedNdc))
        {
            _logger.Warning("PricingWorkflow: cannot normalize NDC '{Ndc}' for verification", ndc);
            return false;
        }

        var deadline = DateTime.UtcNow + ElementTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var candidates = editWindow.FindAllDescendants(cf.ByControlType(ControlType.Edit))
                    .Concat(editWindow.FindAllDescendants(cf.ByControlType(ControlType.Text)));

                foreach (var el in candidates)
                {
                    var raw = el.AsTextBox()?.Text ?? el.Name ?? "";
                    if (string.IsNullOrEmpty(raw)) continue;

                    // Guard: the NDC quick-search dropdown returns a red "(Do Not
                    // Use)" duplicate next to the green active item. If the loaded
                    // item screen shows that marker we must NOT price it — fail so
                    // the row gets a clear status instead of inactive pricing.
                    if (PricingGridReader.LooksLikeDoNotUse(raw))
                    {
                        _logger.Warning(
                            "PricingWorkflow: loaded item for NDC {Ndc} is marked Do Not Use — refusing to price",
                            ndc);
                        return false;
                    }

                    // PioneerRx may display the NDC in any supported shape; normalize before compare
                    // to avoid false negatives on 4-4-2 / 5-3-2 layouts.
                    var observed = NdcNormalizer.TryNormalize(raw.Trim());
                    if (observed == normalizedNdc)
                        return true;

                    // Fallback: substring check against digit-only form, for cases where the NDC
                    // is embedded inside a longer descriptor ("NDC 50242-0041-21 — OMEPRAZOLE …")
                    var digitsOnly = new string(raw.Where(char.IsDigit).ToArray());
                    if (digitsOnly.Contains(normalizedNdc, StringComparison.Ordinal))
                        return true;
                }
            }
            catch { }
            Thread.Sleep(300);
        }

        _logger.Warning("PricingWorkflow: NDC {Ndc} not found in loaded item after {Timeout}s",
            ndc, ElementTimeout.TotalSeconds);
        return false;
    }

    private bool ClickPricingTab(Window editWindow, ConditionFactory cf, SelectorResolver resolver)
    {
        try
        {
            var deadline = DateTime.UtcNow + ElementTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var (pricingTab, res) = resolver.FindFirst(editWindow, cf, SelectorStepId.PricingTab, cf.ByName(PricingTabName));
                if (pricingTab != null)
                {
                    LogIfLearned(SelectorStepId.PricingTab, res);
                    pricingTab.AsTabItem()?.Select();
                    Thread.Sleep(500);
                    return true;
                }
                Thread.Sleep(200);
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: ClickPricingTab failed");
            return false;
        }
    }

    /// <summary>
    /// Reads the supplier catalog DataGrid on the Pricing tab and returns the entry
    /// with the lowest Cost Per Unit.
    ///
    /// Columns are resolved by header NAME, not ordinal (Codex M-4). If PioneerRx
    /// reorders, hides, or adds columns, the lookup still finds the right fields.
    /// On schema miss, fails closed with a distinct failure reason — the prior
    /// fallback-to-hardcoded-ordinals path was Codex-flagged as risking wrong
    /// supplier/cost data on a UI revision (data integrity is precedence 1).
    /// </summary>
    private (string supplier, decimal cost)? ReadCheapestSupplier(
        Window editWindow, ConditionFactory cf, out string? failureReason)
    {
        failureReason = null;
        try
        {
            var deadline = DateTime.UtcNow + GridLoadTimeout;
            AutomationElement? grid = null;
            while (DateTime.UtcNow < deadline)
            {
                grid = editWindow.FindFirstDescendant(cf.ByControlType(ControlType.Table))
                    ?? editWindow.FindFirstDescendant(cf.ByControlType(ControlType.DataGrid));
                if (grid != null) break;
                Thread.Sleep(200);
            }

            if (grid == null)
            {
                _logger.Debug("PricingWorkflow: no DataGrid found on Pricing tab");
                failureReason = "Pricing tab DataGrid not found";
                return null;
            }

            // Virtualized DevExpress grids load rows lazily — reading once can
            // catch a partial set and miss the true cheapest. Wait until the row
            // count stops changing before reading.
            var rows = WaitForStableRows(grid, cf);
            if (rows.Length == 0)
            {
                _logger.Debug("PricingWorkflow: Pricing grid has no rows");
                failureReason = "Pricing grid has no rows";
                return null;
            }

            var cols = ResolvePricingColumns(grid, cf);
            if (cols is null)
            {
                // Fail-fast (Codex review): the schema didn't resolve, so we
                // cannot safely read cells by ordinal without risking a wrong-
                // column write back to the operator's Excel. Surface a clear
                // failure reason so the row's Status cell explains the miss.
                failureReason = "Pricing grid schema not recognized — Supplier/Cost columns missing or renamed";
                return null;
            }
            var (supplierIdx, costIdx, statusIdx) = cols.Value;

            var parsed = new List<PricingGridReader.SupplierRow>(rows.Length);
            foreach (var row in rows)
            {
                var cells = row.FindAllChildren(cf.ByControlType(ControlType.Custom))
                    .Concat(row.FindAllChildren(cf.ByControlType(ControlType.DataItem)))
                    .ToArray();

                var needed = Math.Max(supplierIdx, Math.Max(costIdx, statusIdx));
                if (cells.Length <= needed) continue;

                // Read FULL cell text (Value / LegacyIAccessible pattern), not the
                // rendered Name — supplier names truncate in the grid
                // ("Mckesson Geri…") and a truncated name would be written back.
                var supplierText = GetCellText(cells[supplierIdx]);
                var costText = GetCellText(cells[costIdx]);
                var statusText = statusIdx >= 0 ? GetCellText(cells[statusIdx]) : "";

                if (!PricingGridReader.TryParseCost(costText, out var cost)) continue;
                parsed.Add(new PricingGridReader.SupplierRow(supplierText, cost, statusText));
            }

            // Cheapest = min Cost across ALL usable rows (sort is user-toggleable;
            // never trust row 1) — discontinued/unavailable rows excluded.
            var cheapest = PricingGridReader.SelectCheapest(parsed);
            if (cheapest == null)
            {
                failureReason = "No usable supplier rows in Pricing tab";
                return null;
            }
            return cheapest;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: ReadCheapestSupplier error");
            failureReason = "Pricing grid read error";
            return null;
        }
    }

    /// <summary>
    /// Resolves the Supplier and Cost Per Unit column indices by header name.
    /// WPF DataGrid exposes headers as Header/HeaderItem control types.
    ///
    /// Codex review (2026-05-18) flagged the prior fallback-to-hardcoded-ordinals
    /// behavior: if PioneerRx ships a UI revision that reorders or renames the
    /// pricing grid columns, hardcoded ordinals (5, 10) would silently write
    /// the wrong cell values back to the operator's Excel sheet — a correctness
    /// bug masquerading as success. Pricing data integrity is precedence 1 here.
    ///
    /// Now: header miss returns null. Caller treats null as a typed failure
    /// ("Pricing grid schema not recognized") so the row gets a clear error in
    /// the Excel output instead of plausible-looking wrong data.
    /// </summary>
    private (int supplierIdx, int costIdx, int statusIdx)? ResolvePricingColumns(AutomationElement grid, ConditionFactory cf)
    {
        try
        {
            // Look for a Header descendant (WPF DataGrid exposes column headers as Header control)
            var header = grid.FindFirstDescendant(cf.ByControlType(ControlType.Header));
            if (header == null)
            {
                _logger.Warning("PricingWorkflow: no Header found in grid — failing closed (no ordinal fallback)");
                return null;
            }

            var headerCells = header.FindAllDescendants(cf.ByControlType(ControlType.HeaderItem));
            if (headerCells.Length == 0)
            {
                _logger.Warning("PricingWorkflow: Header has no HeaderItems — failing closed (no ordinal fallback)");
                return null;
            }

            // statusIdx is OPTIONAL — when present we honor "Include Discontinued
            // = No" defensively by skipping discontinued/unavailable rows even if
            // the grid's own filter bar wasn't pinned. Absence (-1) is fine.
            int supplierIdx = -1, costIdx = -1, statusIdx = -1;
            for (int i = 0; i < headerCells.Length; i++)
            {
                var name = headerCells[i].Name?.Trim() ?? "";
                if (supplierIdx == -1 && name.Equals("Supplier", StringComparison.OrdinalIgnoreCase))
                    supplierIdx = i;
                else if (costIdx == -1 &&
                         (name.Equals("Cost Per Unit", StringComparison.OrdinalIgnoreCase) ||
                          name.Equals("Cost (per unit)", StringComparison.OrdinalIgnoreCase)))
                    costIdx = i;
                else if (statusIdx == -1 && name.Equals("Status", StringComparison.OrdinalIgnoreCase))
                    statusIdx = i;
            }

            if (supplierIdx == -1 || costIdx == -1)
            {
                _logger.Warning(
                    "PricingWorkflow: could not resolve Supplier/Cost columns by header name " +
                    "(Supplier={Sup}, Cost={Cost}) — failing closed (no ordinal fallback)",
                    supplierIdx, costIdx);
                return null;
            }

            _logger.Debug("PricingWorkflow: resolved columns — Supplier=col {Sup}, Cost Per Unit=col {Cost}, Status=col {Status}",
                supplierIdx, costIdx, statusIdx);
            return (supplierIdx, costIdx, statusIdx);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PricingWorkflow: column resolution error — failing closed (no ordinal fallback)");
            return null;
        }
    }

    /// <summary>
    /// Polls the grid's row count until it stabilizes (two consecutive equal,
    /// non-zero reads) or the load timeout elapses, then returns the rows.
    /// DevExpress grids virtualize — a single read can catch a partial set and
    /// miss the true cheapest supplier.
    /// </summary>
    private AutomationElement[] WaitForStableRows(AutomationElement grid, ConditionFactory cf)
    {
        var deadline = DateTime.UtcNow + GridLoadTimeout;
        var rows = grid.FindAllChildren(cf.ByControlType(ControlType.DataItem));
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
            var next = grid.FindAllChildren(cf.ByControlType(ControlType.DataItem));
            if (next.Length > 0 && next.Length == rows.Length)
            {
                if (++stable >= 2) { rows = next; break; }
            }
            else
            {
                stable = 0;
            }
            rows = next;
        }
        return rows;
    }

    /// <summary>
    /// Reads a cell's FULL value rather than its rendered Name. Grid cells
    /// truncate long text in the Name property ("Mckesson Geri…"); the
    /// ValuePattern / LegacyIAccessible value carries the complete string.
    /// Falls back to Name (the prior behavior) when no value pattern exists.
    /// </summary>
    private static string GetCellText(AutomationElement el)
    {
        try
        {
            var text = el.AsTextBox()?.Text;
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch { /* not a value-bearing element */ }

        try
        {
            var legacy = el.Patterns.LegacyIAccessible.PatternOrDefault;
            var v = legacy?.Value?.ValueOrDefault;
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        catch { /* legacy pattern unsupported */ }

        return el.Name?.Trim() ?? "";
    }

    private void TryCloseEditWindow(Window editWindow, ConditionFactory cf)
    {
        try
        {
            // Press Escape to dismiss — PioneerRx uses Escape to close dialogs
            editWindow.Focus();
            Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
            Thread.Sleep(300);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: could not close Edit Rx Item window");
        }
    }

    private static SupplierPriceResult Fail(NdcPricingRequest req, string error) =>
        new(req.JobId, req.RowIndex, req.Ndc, false, null, null, error);
}
