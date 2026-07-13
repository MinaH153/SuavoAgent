using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;

namespace SuavoAgent.Helper.Workflows;

public sealed partial class PricingWorkflow
{
    // Header identity is authoritative. Ordinal fallback could silently select
    // the wrong supplier after a PioneerRx layout change, so schema drift fails closed.
    private static readonly UiaGridReader.ColumnSpec[] PricingColumns =
    {
        new("supplier", new[] { "Supplier" }, Required: true),
        new("cost", new[] { "Cost Per Unit", "Cost (per unit)" }, Required: true),
        new("status", new[] { "Status" }, Required: false),
    };

    private (string supplier, decimal costPerUnit)? ReadCheapestSupplier(
        Window editWindow,
        ConditionFactory conditionFactory,
        out string? failureReason)
    {
        failureReason = null;
        try
        {
            var reader = new UiaGridReader(
                _logger,
                GridLoadTimeout,
                ensureLiveActuation: EnsureLiveActuation,
                executeLiveMutation: ExecuteLiveMutation);

            var grid = reader.FindGrid(editWindow, conditionFactory);
            if (grid is null)
            {
                _logger.Debug("PricingWorkflow: no DataGrid found on Pricing tab");
                failureReason = "Pricing tab DataGrid not found";
                return null;
            }

            var rows = reader.WaitForStableRows(grid, conditionFactory, out var expectedRowCount);
            if (rows.Length == 0)
            {
                _logger.Debug("PricingWorkflow: Pricing grid has no rows");
                failureReason = "Pricing grid has no rows";
                return null;
            }

            if (expectedRowCount > rows.Length)
            {
                _logger.Warning(
                    "PricingWorkflow: realized {Read}/{Total} pricing rows; refusing a partial rank",
                    rows.Length,
                    expectedRowCount);
                failureReason =
                    $"Pricing grid virtualized: read {rows.Length} of {expectedRowCount} supplier rows — refusing to rank a partial set";
                return null;
            }

            var columns = reader.ResolveColumns(grid, conditionFactory, PricingColumns);
            if (columns is null)
            {
                failureReason =
                    "Pricing grid schema not recognized — Supplier/Cost columns missing or renamed";
                return null;
            }

            var supplierIndex = columns["supplier"];
            var costIndex = columns["cost"];
            var statusIndex = columns.TryGetValue("status", out var resolvedStatus) ? resolvedStatus : -1;
            var parsed = new List<PricingGridReader.SupplierRow>(rows.Length);

            foreach (var row in rows)
            {
                var cells = UiaGridReader.RowCells(row, conditionFactory);
                var needed = Math.Max(supplierIndex, Math.Max(costIndex, statusIndex));
                if (cells.Length <= needed)
                    continue;

                var supplierText = UiaGridReader.GetCellText(cells[supplierIndex]);
                var costText = UiaGridReader.GetCellText(cells[costIndex]);
                var statusText = statusIndex >= 0
                    ? UiaGridReader.GetCellText(cells[statusIndex])
                    : string.Empty;

                if (PricingGridReader.TryParseCost(costText, out var cost))
                    parsed.Add(new PricingGridReader.SupplierRow(supplierText, cost, statusText));
            }

            _logger.Debug(
                "ReadCheapest: {GridRows} grid rows yielded {Parsed} usable rows",
                rows.Length,
                parsed.Count);

            var cheapest = PricingGridReader.SelectCheapest(parsed);
            if (cheapest is null)
            {
                failureReason = "No usable supplier rows in Pricing tab";
                return null;
            }

            return cheapest;
        }
        catch (PricingActuationGateClosedException)
        {
            throw;
        }
        catch (Exception)
        {
            _logger.Debug("PricingWorkflow: ReadCheapestSupplier failed locally");
            failureReason = "Pricing grid read error";
            return null;
        }
    }
}
