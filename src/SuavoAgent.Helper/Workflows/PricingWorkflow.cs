using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA2;
using Serilog;
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
        var mainWindow = _engine.MainWindow;
        if (mainWindow == null)
            return Fail(request, "PioneerRx main window not available");

        try
        {
            using var automation = new UIA2Automation();
            var cf = automation.ConditionFactory;

            // Step 1: Open Item → Rx Item from the menu bar
            if (!OpenRxItemDialog(mainWindow, cf))
                return Fail(request, "Could not open Item → Rx Item menu");

            // Step 2: Find the Edit Rx Item window
            var editWindow = WaitForWindow(automation, EditRxItemWindowTitle);
            if (editWindow == null)
                return Fail(request, "Edit Rx Item window did not appear");

            try
            {
                // Step 3: Type NDC into Quick Search and press Enter
                if (!SearchByNdc(editWindow, cf, request.Ndc))
                    return Fail(request, $"Could not enter NDC {request.Ndc} in Quick Search");

                // [C-3] Verify the loaded item's NDC matches the requested NDC before reading pricing.
                // Prevents returning pricing data for the previously-selected item when Quick Search
                // is slow or finds no match.
                if (!VerifyLoadedNdc(editWindow, cf, request.Ndc))
                    return Fail(request, $"Loaded item NDC does not match {request.Ndc} — item may not exist or search timed out");

                // Step 4: Navigate to Pricing tab
                if (!ClickPricingTab(editWindow, cf))
                    return Fail(request, "Could not click Pricing tab");

                // Step 5: Read the supplier grid — find cheapest (lowest cost per unit)
                var cheapest = ReadCheapestSupplier(editWindow, cf, out var gridFailure);
                if (cheapest == null)
                    return new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                        false, null, null, gridFailure ?? "No supplier rows found in Pricing tab");

                _logger.Debug("PricingWorkflow: NDC {Ndc} → {Supplier} @ {Cost}/unit",
                    request.Ndc, cheapest.Value.supplier, cheapest.Value.cost);

                return new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                    true, cheapest.Value.supplier, cheapest.Value.cost, null);
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
            return Fail(request, ex.Message);
        }
    }

    private bool OpenRxItemDialog(Window mainWindow, ConditionFactory cf)
    {
        try
        {
            // Click "Item" in the menu bar
            var menuBar = mainWindow.FindFirstDescendant(cf.ByControlType(ControlType.MenuBar));
            if (menuBar == null) return false;

            var itemMenu = menuBar.FindFirstDescendant(cf.ByName(ItemMenuName));
            if (itemMenu == null) return false;

            itemMenu.AsMenuItem()?.Click();
            Thread.Sleep(300);

            // Click "Rx Item" in the dropdown
            var rxItemEntry = mainWindow.FindFirstDescendant(cf.ByName(RxItemMenuName));
            if (rxItemEntry == null) return false;

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

    private bool SearchByNdc(Window editWindow, ConditionFactory cf, string ndc)
    {
        try
        {
            // Quick Search is a text box near the top of the Edit Rx Item window
            var deadline = DateTime.UtcNow + ElementTimeout;
            AutomationElement? searchBox = null;
            while (DateTime.UtcNow < deadline)
            {
                // Try by name hint and by control type + position
                searchBox = editWindow.FindFirstDescendant(
                    new FlaUI.Core.Conditions.AndCondition(
                        cf.ByControlType(ControlType.Edit),
                        cf.ByHelpText(QuickSearchHint)));

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

    private bool ClickPricingTab(Window editWindow, ConditionFactory cf)
    {
        try
        {
            var deadline = DateTime.UtcNow + ElementTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var pricingTab = editWindow.FindFirstDescendant(cf.ByName(PricingTabName));
                if (pricingTab != null)
                {
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

            var rows = grid.FindAllChildren(cf.ByControlType(ControlType.DataItem));
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
            var (supplierIdx, costIdx) = cols.Value;

            string? bestSupplier = null;
            decimal bestCost = decimal.MaxValue;

            foreach (var row in rows)
            {
                var cells = row.FindAllChildren(cf.ByControlType(ControlType.Custom))
                    .Concat(row.FindAllChildren(cf.ByControlType(ControlType.DataItem)))
                    .ToArray();

                if (cells.Length <= Math.Max(supplierIdx, costIdx)) continue;

                var supplierText = cells[supplierIdx].Name?.Trim() ?? "";
                var costText = cells[costIdx].Name?.Trim() ?? "";

                if (string.IsNullOrEmpty(supplierText)) continue;
                if (!decimal.TryParse(costText,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var cost))
                    continue;

                if (cost > 0 && cost < bestCost)
                {
                    bestCost = cost;
                    bestSupplier = supplierText;
                }
            }

            if (bestSupplier == null)
            {
                failureReason = "No supplier rows found in Pricing tab";
                return null;
            }
            return (bestSupplier, bestCost);
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
    private (int supplierIdx, int costIdx)? ResolvePricingColumns(AutomationElement grid, ConditionFactory cf)
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

            int supplierIdx = -1, costIdx = -1;
            for (int i = 0; i < headerCells.Length; i++)
            {
                var name = headerCells[i].Name?.Trim() ?? "";
                if (supplierIdx == -1 && name.Equals("Supplier", StringComparison.OrdinalIgnoreCase))
                    supplierIdx = i;
                else if (costIdx == -1 &&
                         (name.Equals("Cost Per Unit", StringComparison.OrdinalIgnoreCase) ||
                          name.Equals("Cost (per unit)", StringComparison.OrdinalIgnoreCase)))
                    costIdx = i;
            }

            if (supplierIdx == -1 || costIdx == -1)
            {
                _logger.Warning(
                    "PricingWorkflow: could not resolve Supplier/Cost columns by header name " +
                    "(Supplier={Sup}, Cost={Cost}) — failing closed (no ordinal fallback)",
                    supplierIdx, costIdx);
                return null;
            }

            _logger.Debug("PricingWorkflow: resolved columns — Supplier=col {Sup}, Cost Per Unit=col {Cost}",
                supplierIdx, costIdx);
            return (supplierIdx, costIdx);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PricingWorkflow: column resolution error — failing closed (no ordinal fallback)");
            return null;
        }
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
