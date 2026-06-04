namespace SuavoAgent.Contracts.Pricing;

/// <summary>
/// Supplies the two M1 savings inputs the cheapest-supplier lookup does NOT itself know:
/// the pharmacy's CURRENT (baseline) cost per NDC, and the aggregate dispensed quantity.
/// The cloud then computes <c>savings_total = (baseline − sourced) × quantity</c>.
///
/// <para><b>Modality-agnostic by design.</b> A SQL implementation reads PioneerRx
/// (<c>Prescription.RxTransaction.DispensedQuantity</c> for volume + a discovered
/// acquisition-cost column for baseline); a Vision implementation reads the Pricing tab the
/// pharmacist actually sees (often the more-correct source, since the raw SQL catalog has no
/// "this is the supplier I order from" flag). They plug into the same seam — whichever sees
/// truth wins, and the agent learns which over time.</para>
///
/// <para><b>Null is always safe.</b> Either field may be null when unresolved; the runner leaves
/// it off the result, the cloud stores <c>savings_total = NULL</c>, and the card shows count-only
/// — never a wrong dollar figure (Tesla quality: the number we show must be real).</para>
/// </summary>
public interface IPharmacyBaselineVolumeProvider
{
    Task<PharmacyBaselineVolume> GetAsync(string ndc11, CancellationToken ct);
}

/// <summary>Per-NDC baseline cost + aggregate dispensed quantity. Either may be null (unresolved).</summary>
public sealed record PharmacyBaselineVolume(decimal? BaselineCostPerUnit, decimal? Quantity)
{
    /// <summary>Nothing resolved — the safe default.</summary>
    public static readonly PharmacyBaselineVolume None = new(null, null);
}
