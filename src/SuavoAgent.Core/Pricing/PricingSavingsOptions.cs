namespace SuavoAgent.Core.Pricing;

/// <summary>
/// The savings-enrichment knobs handed to <see cref="SqlPricingJobRunner"/> when M1 savings is
/// enabled (and unit-safe). Bundles the workbook column hints with the plausibility-guard caps so
/// the runner constructor stays narrow. Null = no savings enrichment (today's cheapest-cost-only run).
/// </summary>
public sealed record PricingSavingsOptions(
    string? BaselineColumnHint,
    string? QuantityColumnHint,
    decimal MaxUnitCost,
    decimal MaxQuantity,
    decimal SuspiciousSavingsFraction);
