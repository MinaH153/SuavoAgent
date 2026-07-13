namespace SuavoAgent.Contracts.Learning;

/// <summary>
/// GREEN-tier structural atom for a UIA element observed during a workflow step.
/// Looser than <see cref="SuavoAgent.Contracts.Behavioral.ElementSignature"/> — the
/// AutomationId is optional here — so capture can record anonymous PioneerRx controls
/// (the fragile ones searched by Name/HelpText) without throwing. A distributable
/// <c>SelectorPatch</c> target (M2b) keeps the strict ElementSignature; this type exists
/// only to record what was actually on screen.
///
/// PHI boundary: ControlType / AutomationId / ClassName ONLY. Never Name / Value / Text /
/// HelpText / ItemStatus — those are the YELLOW/RED tier and may carry PHI. These are still
/// ordinary strings, so the type does not make the corpus PHI-negative by itself. The local
/// exporter and cloud ingest must both reject non-structural or PHI-shaped values.
/// </summary>
public sealed record ObservedElement(
    string ControlType,
    string? AutomationId,
    string? ClassName);

/// <summary>The updatable selector points in the PioneerRx pricing workflow.</summary>
public enum SelectorStepId
{
    OpenItemMenu,
    OpenRxItem,
    QuickSearchField,
    VerifyNdc,
    PricingTab,
    SupplierGrid,
}

/// <summary>Which source supplied the selector the step actually used.</summary>
public enum SelectorResolvedVia
{
    /// <summary>The hardcoded constant (today's only source; the fail-closed default).</summary>
    Builtin,
    /// <summary>A learned, signed SelectorPatch (M2b+).</summary>
    Learned,
}

/// <summary>How the step resolved its target element.</summary>
public enum SelectorOutcome
{
    /// <summary>The primary selector found the element.</summary>
    Resolved,
    /// <summary>The primary selector missed; a fallback found the element.</summary>
    FallbackUsed,
    /// <summary>No selector found the element — the step failed.</summary>
    Failed,
}

/// <summary>Category of a step failure — the structured learning signal (no free text).</summary>
public enum SelectorFailureKind
{
    None,
    ElementNotFound,
    VerifyMismatch,
    FocusLost,
    Timeout,
    GridEmpty,
}

/// <summary>
/// One labeled example: how a single workflow step resolved its selector, plus the GREEN-tier
/// structural atoms it saw. Emitted by the Helper during a pricing run, ridden back to Core on
/// <c>SupplierPriceResult.Observations</c>, and uploaded to the fleet corpus. The learning signal
/// is the (StepId, Outcome, FailureKind) tuple plus the observed structural candidates — never a
/// human string, NDC, or exception message. Cloud upload independently enforces the bounded
/// structural grammar before any candidate leaves the workstation.
/// </summary>
public sealed record SelectorObservation(
    SelectorStepId StepId,
    SelectorResolvedVia ResolvedVia,
    SelectorOutcome Outcome,
    SelectorFailureKind FailureKind,
    ObservedElement? Attempted,
    IReadOnlyList<ObservedElement> ObservedCandidates);
