namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Builds the reasoning objective that makes the on-device navigator DRIVE the pricing navigation — the
/// perceive→reason→act loop reasons its way to the Supplier-Catalog grid for an NDC instead of firing the
/// hardcoded PricingWorkflow selectors. This is the money-SAFE split: the navigator adapts the NAVIGATION
/// to whatever PioneerRx version is on screen; the deterministic grid read + reconcile still owns the
/// actual price (the reasoner never "reads a cost by reasoning"). taskKey banks/replays the learned
/// trajectory + thickens the Physarum conductance under one stable key.
/// </summary>
public static class PricingNavObjective
{
    /// <summary>The taskKey the navigate loop banks the pricing-nav skill + reinforcement under.</summary>
    public const string TaskKey = "pricing_nav";

    /// <summary>
    /// A loose NDC-shape check so a garbage/injected value fails fast at the command boundary instead of
    /// (a) burning a full navigate loop hunting a nonexistent item and (b) putting arbitrary free text into
    /// the objective the on-device reasoner reads. Not a security boundary — the command is already signed;
    /// this is defense-in-depth + fail-fast. NDCs are 8–13 digits, optionally dash/space segmented.
    /// ponytail: loose digit check; tighten to segmented 4-4-2 / 5-4-2 if quick-search needs an exact form.
    /// </summary>
    public static bool IsPlausibleNdc(string? ndc)
    {
        if (string.IsNullOrWhiteSpace(ndc)) return false;
        if (ndc.Length > 20) return false; // real NDC ≤13 digits +2 separators; caps a padded value from bloating the objective
        int digits = 0;
        foreach (var c in ndc)
        {
            if (char.IsDigit(c)) digits++;
            else if (c is not ('-' or ' ')) return false; // only digits + segment separators
        }
        return digits is >= 8 and <= 13;
    }

    /// <summary>
    /// A goal-oriented objective (not a hardcoded click script) so the reasoner figures out the path on
    /// whatever version of PioneerRx it sees. Describes the END STATE + the known landmarks; success is the
    /// Supplier-Catalog grid being visible for this NDC, at which point the deterministic read takes over.
    /// </summary>
    public static string Build(string ndc)
    {
        var n = (ndc ?? string.Empty).Trim();
        return
            $"In PioneerRx, get to the Supplier-Catalog pricing grid for NDC {n}. " +
            "From the main window open the Item menu, then Rx Item; type the NDC into Quick Search and " +
            "confirm the Edit Rx Item window has loaded that NDC; then open the Pricing tab. " +
            $"Done when the Pricing tab's supplier grid is visible for NDC {n}. " +
            "Only navigate — never change a value, price, or setting.";
    }
}
