namespace SuavoAgent.Contracts.Worklists;

// ===================================================================================================
// Short-Dated / Expiring Stock Worklist — contracts. Reads the lot/expiration inventory grid and lists
// on-hand lots expiring within N days, ranked by dollars at risk, tagged return-eligible, so staff can
// return-for-credit before the wholesaler window closes (and pull already-expired stock). Non-PHI.
// ===================================================================================================

/// <summary>One on-hand inventory lot as the lot/expiration grid exposes it.</summary>
public readonly record struct InventoryLot(
    string Ndc, string Description, string Lot, DateOnly Expiration, decimal QtyOnHand, decimal UnitCost);

/// <summary>A lot that needs action. <c>Flag</c> = EXPIRED (already past dating — pull) or SHORT_DATED
/// (within the window — return for credit while still eligible). <c>ReturnEligible</c> reflects the
/// wholesaler's minimum-dating rule (returns usually require some months of remaining shelf life).</summary>
public readonly record struct ExpiringLine(
    string Ndc,
    string Description,
    string Lot,
    DateOnly Expiration,
    int DaysToExpiry,
    decimal QtyOnHand,
    decimal DollarsAtRisk,
    string Flag,
    bool ReturnEligible);

/// <summary>Expiring-stock flags.</summary>
public static class ExpiringFlags
{
    public const string Expired = "EXPIRED";        // expiration already passed — pull from shelf
    public const string ShortDated = "SHORT_DATED"; // within the alert window — act before it expires
}
