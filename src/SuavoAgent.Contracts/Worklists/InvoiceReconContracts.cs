namespace SuavoAgent.Contracts.Worklists;

// ===================================================================================================
// Wholesaler Invoice-vs-Received Reconciliation — contracts. Joins the wholesaler's invoice (an Excel
// the pharmacy already has) against what PioneerRx actually RECEIVED, flagging quantity shorts and
// unit-cost overcharges with recoverable dollars. The invoice side is a file the pharmacist provides;
// the received side is the box-mapped PioneerRx read. Non-PHI (item + money only).
// ===================================================================================================

/// <summary>One line off the wholesaler's invoice.</summary>
public readonly record struct InvoiceLine(
    string Ndc, string Description, decimal QtyInvoiced, decimal UnitCostInvoiced);

/// <summary>What PioneerRx recorded as received for an NDC. <c>UnitCostExpected</c> is the pharmacy's
/// contract/expected unit cost (from the received record or the item's cost field) — the baseline an
/// overcharge is measured against.</summary>
public readonly record struct ReceivedLine(
    string Ndc, decimal QtyReceived, decimal UnitCostExpected);

/// <summary>A reconciliation exception worth a phone call to the wholesaler. <c>Flag</c> is one of
/// NOT_RECEIVED / SHORT_SHIP / OVERCHARGE. <c>RecoverableDollars</c> is the credit the pharmacy should
/// pursue — the whole point of the sheet.</summary>
public readonly record struct ReconLine(
    string Ndc,
    string Description,
    string Flag,
    decimal QtyInvoiced,
    decimal QtyReceived,
    decimal UnitCostInvoiced,
    decimal UnitCostExpected,
    decimal RecoverableDollars);

/// <summary>Invoice-recon flags — only exceptions are emitted (a clean line is not on the worklist).</summary>
public static class ReconFlags
{
    public const string NotReceived = "NOT_RECEIVED";   // invoiced but PioneerRx never received it
    public const string ShortShip = "SHORT_SHIP";       // received less than invoiced
    public const string Overcharge = "OVERCHARGE";      // invoiced unit cost above the expected/contract cost
}
