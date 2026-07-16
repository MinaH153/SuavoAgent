namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Reconciles the VISION read of the cheapest supplier (what SuavoAgent SEES via OCR — the primary,
/// works-on-any-PMS driver) against the EXACT read (UIA/SQL cell value). This is the money-safety rung:
/// pricing drives what Nadim orders, so an OCR misread must NEVER silently write a wrong cost.
///
/// Policy:
///  • Both present + same supplier + cost within tolerance → accept the EXACT value (vision located it,
///    the exact source confirms the number). Highest confidence.
///  • Both present + they DISAGREE (different supplier, or the cost magnitude is off) → FAIL CLOSED by
///    default (surface for review) — never guess. Configurable to trust the exact source instead.
///  • Vision only (a PMS that doesn't expose the grid to UIA) → accept if OCR confidence clears the
///    floor, else fail closed. This is the pure-vision path.
///  • Exact only (vision unavailable) → accept the exact value (degraded but still correct).
///
/// Pure + unit-testable.
/// </summary>
public static class VisionExactReconciler
{
    public enum MismatchPolicy
    {
        /// <summary>Disagreement → reject (don't write). Correctness over coverage — the pilot default.</summary>
        FailClosed,
        /// <summary>Disagreement → trust the exact source (it is authoritative), log the mismatch.</summary>
        TrustExact,
    }

    public readonly record struct Reading(string Supplier, decimal CostPerUnit, double Confidence);

    public sealed record Decision(
        bool Accept,
        string? Supplier,
        decimal? CostPerUnit,
        string Source,
        double Confidence,
        string? RejectReason)
    {
        public static Decision Accepted(string supplier, decimal cost, string source, double confidence) =>
            new(true, supplier, cost, source, confidence, null);

        public static Decision Rejected(string reason) =>
            new(false, null, null, "none", 0, reason);
    }

    public static Decision Reconcile(
        Reading? vision,
        Reading? uia,
        double minVisionConfidence = 0.6,
        double costTolerance = 0.02,
        MismatchPolicy policy = MismatchPolicy.FailClosed)
    {
        if (vision is null && uia is null)
            return Decision.Rejected("no_reading");

        if (vision is { } v && uia is { } u)
        {
            var sameSupplier = SupplierMatches(v.Supplier, u.Supplier);
            var costClose = CostWithinTolerance(v.CostPerUnit, u.CostPerUnit, costTolerance);
            if (sameSupplier && costClose)
                // Vision saw it; the exact source confirms the number → write the EXACT value.
                return Decision.Accepted(u.Supplier, u.CostPerUnit, "vision+exact",
                    Math.Min(1.0, v.Confidence + 0.2));

            // Reject reasons cross the Helper/Core boundary and may be persisted or uploaded.
            // Never echo OCR/UIA values here: OCR text is untrusted screen content and could
            // contain PHI if the foreground surface changed unexpectedly.
            const string reason = "vision_exact_mismatch";
            return policy == MismatchPolicy.TrustExact
                ? Decision.Accepted(u.Supplier, u.CostPerUnit, "exact_over_vision_mismatch", 0.5)
                : Decision.Rejected(reason);
        }

        if (uia is { } exactOnly)
            // Vision didn't contribute (capture/OCR unavailable) — the exact source is still authoritative.
            return Decision.Accepted(exactOnly.Supplier, exactOnly.CostPerUnit, "exact_only", 0.9);

        // Vision only — no exact source to confirm against (a PMS without a UIA-exposed grid). Gate on
        // OCR confidence so a fuzzy read never writes a cost with nothing backing it.
        var visionOnly = vision!.Value;
        return visionOnly.Confidence >= minVisionConfidence
            ? Decision.Accepted(visionOnly.Supplier, visionOnly.CostPerUnit, "vision_only", visionOnly.Confidence)
            : Decision.Rejected(
                $"low_confidence_vision_no_exact ({visionOnly.Confidence:F2} < {minVisionConfidence:F2})");
    }

    // Forgiving of OCR truncation / extra descriptor words ("Mckesson Drug" vs "McKesson") while still
    // catching a genuine wrong-supplier ("McKesson" vs "Parmed"): the shorter normalized name must be a
    // substring of the longer, with a 3-char floor so trivial fragments don't match.
    private static bool SupplierMatches(string a, string b)
    {
        var na = Norm(a);
        var nb = Norm(b);
        if (na.Length < 3 || nb.Length < 3) return na == nb;
        return na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal);
    }

    private static string Norm(string s) =>
        new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool CostWithinTolerance(decimal a, decimal b, double tol)
    {
        if (b == 0m) return a == 0m;
        var rel = (double)Math.Abs(a - b) / (double)Math.Abs(b);
        return rel <= tol;
    }
}
