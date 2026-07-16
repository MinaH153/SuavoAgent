using System.Security.Cryptography;
using System.Text;
using System.Collections.Frozen;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// PHI-negative boundary for pricing-result persistence and transport. Workbook
/// cells are untrusted even when their header says NDC; only a validated,
/// canonical 11-digit NDC may survive beyond the workbook.
/// </summary>
internal static class PricingResultContentPolicy
{
    internal const string InvalidNdcStorageValue = "";
    internal const string InvalidNdcReasonCode = "invalid_ndc";
    internal const string NumericCapacityReviewCode =
        "pricing_savings_numeric_capacity_review_required";
    internal const decimal MaximumUnitCost = 99_999_999.9999m;
    internal const decimal MaximumQuantity = 99_999_999_999.999m;
    internal const decimal MaximumSavingsTotal = 9_999_999_999.9999m;
    private const string OpaqueSupplierPrefix = "supplier:";
    private static readonly FrozenDictionary<string, string> ApprovedSupplierNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Cardinal Health"] = "Cardinal Health",
            ["Real Value Rx"] = "Real Value Rx",
            ["McKesson"] = "McKesson",
            ["McKesson Pharmaceutical"] = "McKesson Pharmaceutical",
            ["Cencora"] = "Cencora",
            ["AmerisourceBergen"] = "Cencora",
            ["Morris & Dickson"] = "Morris & Dickson",
            ["Anda"] = "Anda",
            ["Smith Drug Company"] = "Smith Drug Company",
            ["Rochester Drug Cooperative"] = "Rochester Drug Cooperative",
            ["Dakota Drug"] = "Dakota Drug",
            ["Value Drug Company"] = "Value Drug Company",
            ["Masters Pharmaceutical"] = "Masters Pharmaceutical",
            ["KeySource"] = "KeySource",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    internal static SupplierPriceResult NormalizeForPersistence(
        SupplierPriceResult result)
    {
        if (!PricingApprovalContract.IsSupportedCostBasis(result.CostBasis))
            throw new InvalidOperationException("pricing_result_cost_basis_invalid");
        var selectorObservationTotal =
            (long)result.OmittedSelectorObservations +
            (result.Observations?.Count ?? 0);
        if (result.OmittedSelectorObservations < 0 ||
            selectorObservationTotal >
                PricingSelectorObservationPolicy.MaximumTotalObservations)
            throw new InvalidOperationException(
                "pricing_result_selector_observations_out_of_range");
        var canonical = CanonicalNdcOrNull(result.Ndc);
        if (canonical is null)
        {
            // Preserve only the structural row identity and a fixed reason code.
            // Supplier/cost/evidence fields are not meaningful without a proven NDC.
            return new SupplierPriceResult(
                result.JobId,
                result.RowIndex,
                InvalidNdcStorageValue,
                Found: false,
                SupplierName: null,
                CostPerUnit: null,
                ErrorMessage: InvalidNdcReasonCode,
                Observations: null,
                BaselineCostPerUnit: null,
                Quantity: null,
                OmittedSelectorObservations: (int)selectorObservationTotal,
                PackageCost: null,
                CostBasis: result.CostBasis);
        }

        var sourced = QuantizeUnitCost(result.CostPerUnit);
        var package = QuantizeUnitCost(result.PackageCost);
        var baseline = QuantizeUnitCost(result.BaselineCostPerUnit);
        var quantity = QuantizeQuantity(result.Quantity);
        var packageBasis = result.CostBasis == PricingApprovalContract.PackageCostBasis;
        var capacityReviewRequired = packageBasis
            ? result.PackageCost is not null && package is null ||
              result.CostPerUnit is not null ||
              result.BaselineCostPerUnit is not null ||
              result.Quantity is not null
            : result.CostPerUnit is not null && sourced is null ||
              result.PackageCost is not null ||
              result.BaselineCostPerUnit is not null && baseline is null ||
              result.Quantity is not null && quantity is null;

        if (packageBasis)
        {
            sourced = null;
            baseline = null;
            quantity = null;
        }
        else
        {
            package = null;
        }

        if (!capacityReviewRequired &&
            baseline is not null && sourced is not null && quantity is not null &&
            baseline > sourced)
        {
            try
            {
                capacityReviewRequired =
                    (baseline.Value - sourced.Value) * quantity.Value >
                    MaximumSavingsTotal;
            }
            catch (OverflowException)
            {
                capacityReviewRequired = true;
            }
        }

        if (capacityReviewRequired)
        {
            // Keep the sourced result when it is independently representable,
            // but break the savings tuple so the server can never calculate an
            // overflowing NUMERIC(14,4). The fixed local code is reviewable and
            // is intentionally never serialized as a free-text warning.
            baseline = null;
            quantity = null;
        }

        return result with
        {
            Ndc = canonical,
            CostPerUnit = sourced,
            PackageCost = package,
            BaselineCostPerUnit = baseline,
            Quantity = quantity,
            ErrorMessage = capacityReviewRequired
                ? NumericCapacityReviewCode
                : result.ErrorMessage,
        };
    }

    private static decimal? QuantizeUnitCost(decimal? value)
    {
        if (value is null || value is < 0) return null;
        var rounded = decimal.Round(value.Value, 4, MidpointRounding.AwayFromZero);
        return rounded <= MaximumUnitCost ? rounded : null;
    }

    private static decimal? QuantizeQuantity(decimal? value)
    {
        if (value is null || value is < 0) return null;
        var rounded = decimal.Round(value.Value, 3, MidpointRounding.AwayFromZero);
        return rounded <= MaximumQuantity ? rounded : null;
    }

    internal static SupplierPriceResult InvalidNdcRow(
        string jobId,
        int rowIndex,
        string costBasis = PricingApprovalContract.CostPerUnitBasis) =>
        NormalizeForPersistence(new SupplierPriceResult(
            jobId,
            rowIndex,
            InvalidNdcStorageValue,
            Found: false,
            SupplierName: null,
            CostPerUnit: null,
            ErrorMessage: InvalidNdcReasonCode,
            CostBasis: costBasis));

    internal static string? CanonicalNdcOrNull(string? value)
    {
        var outcome = NdcNormalizer.Normalize(value);
        var canonical = outcome.Canonical11;
        return outcome.Ok &&
               canonical is { Length: 11 } &&
               canonical.All(character => character is >= '0' and <= '9')
            ? canonical
            : null;
    }

    internal static bool IsExactCanonicalNdc(string? value) =>
        value is { Length: 11 } &&
        value.All(character => character is >= '0' and <= '9') &&
        string.Equals(CanonicalNdcOrNull(value), value, StringComparison.Ordinal);

    internal static string? CloudSafeSupplierName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Normalize(NormalizationForm.FormKC);
        if (ApprovedSupplierNames.TryGetValue(normalized, out var approved))
            return approved;

        var canonicalForHash = normalized.ToUpperInvariant();
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalForHash)))
            .ToLowerInvariant();
        return OpaqueSupplierPrefix + digest;
    }

    internal static bool IsCloudSafeSupplierName(string? value)
    {
        if (value is null) return true;
        if (ApprovedSupplierNames.Values.Contains(value, StringComparer.Ordinal))
            return true;
        if (!value.StartsWith(OpaqueSupplierPrefix, StringComparison.Ordinal) ||
            value.Length != OpaqueSupplierPrefix.Length + 64)
            return false;

        foreach (var character in value.AsSpan(OpaqueSupplierPrefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }
}

/// <summary>
/// Exact native/server transport ceiling. Required rows reserve a conservative
/// worst-case byte budget so oversized workbooks fail before any PMS lookup;
/// optional selector observations are shed at serialization time first. The
/// pharmacy workflow has a stricter Top-500 domain ceiling, which also bounds
/// the signed command execution SLA independently of transport headroom.
/// </summary>
internal static class PricingResultPayloadBudget
{
    internal const int MaximumSerializedBytes = 2 * 1024 * 1024;
    internal const int MaximumSerializedMetric = 5000;
    private const int ReservedEnvelopeBytes = 4 * 1024;
    private const int ReservedRequiredRowBytes = 1024;
    internal const int MaximumRequiredRows = 500;
    internal const int MaximumTransportRows =
        (MaximumSerializedBytes - ReservedEnvelopeBytes) / ReservedRequiredRowBytes;

    internal static bool CanAdmitRequiredRows(int rowCount) =>
        rowCount >= 0 && rowCount <= MaximumRequiredRows;

    internal static bool CanAdmitWorkload(int requiredRowCount, int totalItems) =>
        CanAdmitRequiredRows(requiredRowCount) &&
        totalItems >= 0 && totalItems <= MaximumSerializedMetric;

    internal static bool AreSerializedMetricsValid(
        int totalItems,
        int completedItems,
        int failedItems,
        int itemCount,
        int omittedInvalidItems) =>
        totalItems is >= 0 and <= MaximumSerializedMetric &&
        completedItems is >= 0 and <= MaximumSerializedMetric &&
        failedItems is >= 0 and <= MaximumSerializedMetric &&
        itemCount is >= 0 and <= MaximumSerializedMetric &&
        omittedInvalidItems is >= 0 and <= MaximumSerializedMetric &&
        itemCount + omittedInvalidItems == totalItems &&
        completedItems + failedItems == totalItems;

    internal static int SerializedSize(string json) =>
        Encoding.UTF8.GetByteCount(json);
}
