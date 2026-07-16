using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper.Workflows;

public sealed partial class PricingWorkflow
{
    private const string IncludeDiscontinuedFilterLabel = "Include Discontinued:";
    private const string IncludeDiscontinuedFilterId = "cmbIncludeDiscontinued";
    private const string InventoryGroupFilterLabel = "Inventory Group:";
    private const string InventoryGroupFilterId = "cmbInventoryGroup";

    private static readonly UiaGridReader.ColumnSpec[] PackagePricingColumns =
    {
        new("linked", new[] { "Linked" }, Required: true),
        new("inventory_group", new[] { "Inventory Group" }, Required: true),
        new("supplier", new[] { "Supplier" }, Required: true),
        new("shipping_size", new[] { "Shipping Size" }, Required: true),
        new("package_cost", new[] { "Cost" }, Required: true),
        new("cost_per_unit", new[] { "Cost Per Unit" }, Required: true),
        new("status", new[] { "Status" }, Required: true),
    };

    private SupplierPriceResult ReadPackageCostResult(
        NdcPricingRequest request,
        Window editWindow,
        ConditionFactory conditionFactory,
        out string? failureReason)
    {
        failureReason = null;
        try
        {
            if (!ApplyPackageFilterContract(editWindow, conditionFactory))
                return PackageFailure(
                    request,
                    "Package pricing filters could not be verified",
                    out failureReason);

            var reader = new UiaGridReader(
                _logger,
                GridLoadTimeout,
                ensureLiveActuation: EnsureLiveActuation,
                executeLiveMutation: ExecuteLiveMutation);
            var grid = reader.FindGrid(editWindow, conditionFactory);
            if (grid is null)
                return PackageFailure(request, "Pricing tab DataGrid not found", out failureReason);

            var rows = reader.WaitForStableRows(
                grid, conditionFactory, out var expectedRowCount);
            if (rows.Length == 0)
                return PackageFailure(request, "Pricing grid has no rows", out failureReason);
            if (expectedRowCount > rows.Length)
                return PackageFailure(
                    request,
                    "Pricing grid is virtualized; refusing to rank a partial supplier set",
                    out failureReason);

            var columns = reader.ResolveColumns(
                grid, conditionFactory, PackagePricingColumns);
            if (columns is null)
                return PackageFailure(
                    request,
                    "Pricing grid package-cost schema not recognized",
                    out failureReason);

            var parsed = new List<PricingGridReader.PackageSupplierRow>(rows.Length);
            foreach (var row in rows)
            {
                var cells = UiaGridReader.RowCells(row, conditionFactory);
                var needed = columns.Values.Max();
                if (cells.Length <= needed) continue;

                var linkedCell = cells[columns["linked"]];
                var inventoryGroup = UiaGridReader.GetCellText(
                    cells[columns["inventory_group"]]);
                var supplier = UiaGridReader.GetCellText(cells[columns["supplier"]]);
                var packageCostText = UiaGridReader.GetCellText(
                    cells[columns["package_cost"]]);
                var status = UiaGridReader.GetCellText(cells[columns["status"]]);
                if (!TryReadLinked(linkedCell, conditionFactory, out var linked) ||
                    !PricingGridReader.TryParseCost(packageCostText, out var packageCost))
                    continue;
                parsed.Add(new PricingGridReader.PackageSupplierRow(
                    supplier,
                    packageCost,
                    status,
                    linked,
                    inventoryGroup,
                    status.Contains("Discontinued", StringComparison.OrdinalIgnoreCase)));
            }

            var cheapest = PricingGridReader.SelectCheapestPackage(parsed);
            if (cheapest is null)
                return PackageFailure(
                    request, "No eligible package-cost supplier rows", out failureReason);

            return new SupplierPriceResult(
                request.JobId,
                request.RowIndex,
                request.Ndc,
                Found: true,
                SupplierName: cheapest.Value.supplier,
                CostPerUnit: null,
                ErrorMessage: null,
                PackageCost: cheapest.Value.packageCost,
                CostBasis: PricingApprovalContract.PackageCostBasis);
        }
        catch (PricingActuationGateClosedException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.Debug("PricingWorkflow: package-cost grid read failed locally");
            return PackageFailure(request, "Pricing grid read error", out failureReason);
        }
    }

    private bool ApplyPackageFilterContract(
        Window editWindow,
        ConditionFactory conditionFactory) =>
        SetAndVerifyExactFilter(
            editWindow,
            conditionFactory,
            IncludeDiscontinuedFilterId,
            IncludeDiscontinuedFilterLabel,
            "No") &&
        SetAndVerifyExactFilter(
            editWindow,
            conditionFactory,
            InventoryGroupFilterId,
            InventoryGroupFilterLabel,
            "Rx");

    private bool SetAndVerifyExactFilter(
        AutomationElement root,
        ConditionFactory conditionFactory,
        string automationId,
        string exactLabel,
        string expected)
    {
        var element = FindFilterCombo(
            root,
            conditionFactory,
            automationId,
            exactLabel);
        var combo = element?.AsComboBox();
        if (combo is null) return false;
        if (ComboHasExactValue(combo, expected)) return true;
        try
        {
            ExecuteLiveMutation(() => combo.Select(expected));
            Thread.Sleep(150);
            return ComboHasExactValue(combo, expected);
        }
        catch (PricingActuationGateClosedException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static AutomationElement? FindFilterCombo(
        AutomationElement root,
        ConditionFactory conditionFactory,
        string automationId,
        string exactLabel)
    {
        var byId = root.FindFirstDescendant(new AndCondition(
            conditionFactory.ByControlType(ControlType.ComboBox),
            conditionFactory.ByAutomationId(automationId)));
        if (byId is not null) return byId;

        var byHelpText = root.FindFirstDescendant(new AndCondition(
            conditionFactory.ByControlType(ControlType.ComboBox),
            conditionFactory.ByHelpText(exactLabel)));
        if (byHelpText is not null) return byHelpText;

        // PioneerRx's DevExpress filter bar exposes the labels but not stable
        // automation ids. Bind each combo to the exact label immediately to
        // its left on the same row; never fall back to combo ordinal.
        var label = root.FindFirstDescendant(
            conditionFactory.ByName(exactLabel));
        if (label is null) return null;
        try
        {
            var labelBounds = label.BoundingRectangle;
            if (labelBounds.IsEmpty) return null;
            return root
                .FindAllDescendants(
                    conditionFactory.ByControlType(ControlType.ComboBox))
                .Select(combo => (combo, bounds: combo.BoundingRectangle))
                .Where(candidate =>
                    !candidate.bounds.IsEmpty &&
                    candidate.bounds.Left >= labelBounds.Right - 4 &&
                    Math.Abs(
                        (candidate.bounds.Top + candidate.bounds.Height / 2) -
                        (labelBounds.Top + labelBounds.Height / 2)) <= 12)
                .OrderBy(candidate => candidate.bounds.Left - labelBounds.Right)
                .Select(candidate => candidate.combo)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool ComboHasExactValue(ComboBox combo, string expected)
    {
        try
        {
            return string.Equals(combo.SelectedItem?.Text?.Trim(), expected, StringComparison.Ordinal) ||
                   string.Equals(combo.Value?.Trim(), expected, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    internal static bool PackageFilterContractMatches(
        string? includeDiscontinued,
        string? inventoryGroup) =>
        string.Equals(includeDiscontinued, "No", StringComparison.Ordinal) &&
        string.Equals(inventoryGroup, "Rx", StringComparison.Ordinal);

    private static SupplierPriceResult PackageFailure(
        NdcPricingRequest request,
        string reason,
        out string? failureReason)
    {
        failureReason = reason;
        return new SupplierPriceResult(
            request.JobId,
            request.RowIndex,
            request.Ndc,
            Found: false,
            SupplierName: null,
            CostPerUnit: null,
            ErrorMessage: reason,
            PackageCost: null,
            CostBasis: PricingApprovalContract.PackageCostBasis);
    }

    private static bool TryReadLinked(
        AutomationElement cell,
        ConditionFactory conditionFactory,
        out bool linked)
    {
        if (PricingGridReader.TryParseLinked(
                UiaGridReader.GetCellText(cell),
                out linked))
            return true;

        try
        {
            var toggle = cell.Patterns.Toggle.IsSupported
                ? cell
                : cell.FindFirstDescendant(
                    conditionFactory.ByControlType(ControlType.CheckBox));
            if (toggle is null || !toggle.Patterns.Toggle.IsSupported)
                return false;
            var state = toggle.Patterns.Toggle.Pattern.ToggleState.Value;
            if (state == ToggleState.Indeterminate) return false;
            linked = state == ToggleState.On;
            return true;
        }
        catch
        {
            linked = false;
            return false;
        }
    }
}
