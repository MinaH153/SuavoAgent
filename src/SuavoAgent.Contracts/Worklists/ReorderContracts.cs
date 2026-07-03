namespace SuavoAgent.Contracts.Worklists;

// ===================================================================================================
// Below-Reorder Purchase Worklist — contracts. Reads PioneerRx's inventory / suggested-order grid,
// flags items at/under their reorder point with nothing already on order, and suggests a buy quantity;
// the flagged NDCs then flow through the SHIPPED pricing pass so the pharmacy gets a purchase-ready,
// best-priced order sheet. Non-PHI (aggregate inventory only — no patient tie). Mirrors Feature A's
// read-grid -> pure-logic -> Excel writeback shape; the live grid read is the box-mapped modality seam.
// ===================================================================================================

/// <summary>One inventory row as the reorder/suggested-order grid exposes it. Quantities are decimals
/// (packs/eaches). <c>CostPerUnit</c> is optional — when the grid carries it we can pre-fill cost, else
/// the pricing pass sources it. All non-PHI item-level fields.</summary>
public readonly record struct ReorderGridRow(
    string Item,
    string Ndc,
    decimal Boh,
    decimal ReorderPoint,
    decimal ReorderQty,
    decimal OnOrder,
    decimal? CostPerUnit = null);

/// <summary>One line of the purchase worklist: an item that needs reordering + how much to buy. The
/// best-supplier / best-cost columns are filled by the pricing pass (reused verbatim); null until sourced.</summary>
public readonly record struct ReorderLine(
    string Item,
    string Ndc,
    decimal Boh,
    decimal ReorderPoint,
    decimal SuggestedQty,
    string? BestSupplier = null,
    decimal? BestCostPerUnit = null);
