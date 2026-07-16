using SuavoAgent.Helper.Workflows;
using Xunit;
using R = SuavoAgent.Helper.Workflows.VisionExactReconciler;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// The reconciler is the money-safety rung: it guarantees an OCR misread never writes a wrong cost.
/// Vision LOCATES the cheapest supplier; the exact (UIA/SQL) source CONFIRMS the number.
/// </summary>
public class VisionExactReconcilerTests
{
    [Fact]
    public void Agreement_writes_the_exact_value_not_the_ocr_value()
    {
        // Same supplier, cost within tolerance → accept, and the written cost is the EXACT one.
        var vision = new R.Reading("Mckesson", 0.0098m, 0.85); // slightly-off OCR cost
        var exact = new R.Reading("McKesson", 0.0099m, 1.0);   // authoritative

        var d = R.Reconcile(vision, exact);

        Assert.True(d.Accept);
        Assert.Equal(0.0099m, d.CostPerUnit); // exact, not the 0.0098 OCR value
        Assert.Equal("vision+exact", d.Source);
    }

    [Fact]
    public void Different_supplier_fails_closed_by_default()
    {
        var d = R.Reconcile(new R.Reading("Parmed", 6.60m, 0.9), new R.Reading("McKesson", 0.0099m, 1.0));

        Assert.False(d.Accept);
        Assert.Equal("vision_exact_mismatch", d.RejectReason);
    }

    [Fact]
    public void Mismatch_reason_never_echoes_untrusted_screen_text()
    {
        var d = R.Reconcile(
            new R.Reading("Patient John Doe", 6.60m, 0.9),
            new R.Reading("McKesson", 0.0099m, 1.0));

        Assert.False(d.Accept);
        Assert.Equal("vision_exact_mismatch", d.RejectReason);
        Assert.DoesNotContain("John", d.RejectReason, StringComparison.Ordinal);
        Assert.DoesNotContain("McKesson", d.RejectReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Same_supplier_but_wildly_off_cost_fails_closed()
    {
        // Right supplier, but the OCR cost is off by orders of magnitude (wrong column read) → reject.
        var d = R.Reconcile(new R.Reading("McKesson", 84.2m, 0.9), new R.Reading("McKesson", 0.0099m, 1.0));

        Assert.False(d.Accept);
        Assert.Contains("mismatch", d.RejectReason!);
    }

    [Fact]
    public void Mismatch_with_TrustExact_policy_writes_the_exact_value()
    {
        var d = R.Reconcile(
            new R.Reading("Parmed", 6.60m, 0.9),
            new R.Reading("McKesson", 0.0099m, 1.0),
            policy: R.MismatchPolicy.TrustExact);

        Assert.True(d.Accept);
        Assert.Equal("McKesson", d.Supplier);
        Assert.Equal(0.0099m, d.CostPerUnit);
    }

    [Fact]
    public void Exact_only_is_accepted_when_vision_is_unavailable()
    {
        var d = R.Reconcile(vision: null, uia: new R.Reading("McKesson", 0.0099m, 1.0));
        Assert.True(d.Accept);
        Assert.Equal("exact_only", d.Source);
    }

    [Fact]
    public void Vision_only_accepted_above_the_confidence_floor()
    {
        var d = R.Reconcile(new R.Reading("McKesson", 0.0099m, 0.80), uia: null, minVisionConfidence: 0.6);
        Assert.True(d.Accept);
        Assert.Equal("vision_only", d.Source);
    }

    [Fact]
    public void Vision_only_below_the_floor_fails_closed()
    {
        var d = R.Reconcile(new R.Reading("McKesson", 0.0099m, 0.40), uia: null, minVisionConfidence: 0.6);
        Assert.False(d.Accept);
        Assert.Contains("low_confidence", d.RejectReason!);
    }

    [Fact]
    public void No_reading_at_all_is_rejected()
    {
        var d = R.Reconcile(vision: null, uia: null);
        Assert.False(d.Accept);
    }

    [Fact]
    public void Fuzzy_supplier_name_still_matches()
    {
        // OCR truncation / extra descriptor word must not cause a false mismatch.
        var d = R.Reconcile(new R.Reading("Mckesson Drug Co", 0.0099m, 0.9), new R.Reading("McKesson", 0.0099m, 1.0));
        Assert.True(d.Accept);
        Assert.Equal("vision+exact", d.Source);
    }
}
