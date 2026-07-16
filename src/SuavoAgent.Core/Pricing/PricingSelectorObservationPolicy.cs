namespace SuavoAgent.Core.Pricing;

internal static class PricingSelectorObservationPolicy
{
    internal const int MaximumIncludedCloudObservations = 6_000;
    internal const int MaximumStoredObservationsPerResult = 6_000;
    internal const int MaximumTotalObservations = 30_000_000;
    internal const int MaximumCandidatesPerObservation = 128;
}
