namespace SuavoAgent.Contracts.Worklists;

// ===================================================================================================
// Will-Call Return-to-Stock Sweep — contracts. Scans the Will Call / pickup queue and flags filled Rxs
// sitting UNCLAIMED past the board's N-day limit, emitting a LOCAL-ONLY return-to-stock pick list.
//
// PHI posture: this touches the dispensing queue (patient-adjacent). It is LOCAL-ONLY — the pick list
// never leaves the box, and it carries the MINIMUM identifier needed to pull the Rx (Rx number + drug),
// NOT patient name/DOB/address. The cloud never sees it (same PHI wall as everything else).
// ===================================================================================================

/// <summary>One will-call entry as the pickup queue exposes it. Minimal identifiers only.</summary>
public readonly record struct WillCallEntry(
    string RxNumber, string Drug, string Ndc, DateOnly FilledDate, decimal Qty);

/// <summary>An Rx to return to stock — unclaimed past the limit. Local-only pick line.</summary>
public readonly record struct ReturnToStockLine(
    string RxNumber, string Drug, string Ndc, DateOnly FilledDate, int DaysUnclaimed, decimal Qty);
