using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA2;
using Serilog;
using SuavoAgent.Contracts.Pricing;

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
                var cheapest = ReadCheapestSupplier(editWindow, cf);
                if (cheapest == null)
                    return new SupplierPriceResult(request.JobId, request.RowIndex, request.Ndc,
                        false, null, null, "No supplier rows found in Pricing tab");

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

            searchBox.Focus();
            Thread.Sleep(100);

            // Clear existing text then type NDC
            Keyboard.TypeSimultaneously(FlaUI.Core.WindowsAPI.VirtualKeyShort.CONTROL,
                FlaUI.Core.WindowsAPI.VirtualKeyShort.KEY_A);
            Thread.Sleep(50);
            Keyboard.Type(ndc);
            Thread.Sleep(100);
            Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);

            // Wait for item to load
            Thread.Sleep(800);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: SearchByNdc failed for {Ndc}", ndc);
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
        // Normalize for comparison: strip hyphens, pad to 11
        var normalizedNdc = ndc.Replace("-", "").PadLeft(11, '0');

        var deadline = DateTime.UtcNow + ElementTimeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Scan all Edit and Text elements for any that contain the NDC
                var candidates = editWindow.FindAllDescendants(cf.ByControlType(ControlType.Edit))
                    .Concat(editWindow.FindAllDescendants(cf.ByControlType(ControlType.Text)));

                foreach (var el in candidates)
                {
                    var val = el.AsTextBox()?.Text?.Replace("-", "") ?? el.Name?.Replace("-", "") ?? "";
                    if (val.Contains(normalizedNdc, StringComparison.OrdinalIgnoreCase))
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
    /// Reads the supplier catalog DataGrid on the Pricing tab.
    /// Returns the entry with the lowest Cost Per Unit.
    /// Grid columns confirmed from screenshot: Name, NDC, Supplier, Cost, Cost Per Unit, Status, ...
    /// The grid is already sorted by the "linked" 340B row first; we find the cheapest by scanning all rows.
    /// </summary>
    private (string supplier, decimal cost)? ReadCheapestSupplier(Window editWindow, ConditionFactory cf)
    {
        try
        {
            // Wait for the DataGrid/Table to load
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
                return null;
            }

            var rows = grid.FindAllChildren(cf.ByControlType(ControlType.DataItem));
            if (rows.Length == 0)
            {
                _logger.Debug("PricingWorkflow: Pricing grid has no rows");
                return null;
            }

            string? bestSupplier = null;
            decimal bestCost = decimal.MaxValue;

            foreach (var row in rows)
            {
                var cells = row.FindAllChildren(cf.ByControlType(ControlType.Custom))
                    .Concat(row.FindAllChildren(cf.ByControlType(ControlType.DataItem)))
                    .ToArray();

                // Column layout from screenshot (0-indexed):
                // 0=Linked, 1=Inventory Group, 2=Name, 3=NDC, 4=UPC, 5=Supplier,
                // 6=Supplier Item Number, 7=Manufacturer, 8=Shipping Size,
                // 9=Cost, 10=Cost Per Unit, ...
                // We want column 5 (Supplier) and column 10 (Cost Per Unit)
                if (cells.Length < 11) continue;

                var supplierText = cells[5].Name?.Trim() ?? "";
                var costText = cells[10].Name?.Trim() ?? "";

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

            return bestSupplier != null ? (bestSupplier, bestCost) : null;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "PricingWorkflow: ReadCheapestSupplier error");
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
