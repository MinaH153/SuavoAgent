using System.Collections.Immutable;
using System.Text.Json;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.Cloud;

public sealed partial class PricingJobCloudUploader
{
    internal static bool IsPersistedPayloadCloudSafe(
        JsonElement payload,
        string expectedJobId,
        int expectedItemCount)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !HasExactPropertySet(payload, BodyKeys) ||
            !payload.TryGetProperty("commandId", out var commandId) ||
            commandId.ValueKind != JsonValueKind.Null &&
                (commandId.ValueKind != JsonValueKind.String ||
                 !SafeEvidenceIdPattern.IsMatch(commandId.GetString() ?? "")) ||
            !TryReadPersistedAuthorityBinding(
                payload, out _, out _) ||
            !payload.TryGetProperty("status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            status.GetString() != PricingJobStatus.Completed ||
            !payload.TryGetProperty("mode", out var mode) ||
            mode.ValueKind != JsonValueKind.String ||
            !SafeSources.Contains(mode.GetString() ?? "") ||
            !payload.TryGetProperty("costBasis", out var costBasis) ||
            costBasis.ValueKind != JsonValueKind.String ||
            !PricingApprovalContract.IsSupportedCostBasis(costBasis.GetString()) ||
            !payload.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() != expectedItemCount ||
            expectedItemCount is < 0 or > PricingResultPayloadBudget.MaximumSerializedMetric ||
            !TryGetPersistedMetric(payload, "totalItems", out var totalItems) ||
            !TryGetPersistedMetric(payload, "completedItems", out var completedItems) ||
            !TryGetPersistedMetric(payload, "failedItems", out var failedItems) ||
            !TryGetPersistedMetric(
                payload, "omittedInvalidItems", out var omittedInvalidItems) ||
            !TryGetBoundedInt(
                payload,
                "omittedSelectorObservations",
                PricingSelectorObservationPolicy.MaximumTotalObservations,
                out var omittedSelectorObservations))
            return false;

        var foundItems = 0;
        var notFoundItems = 0;
        var observationCount = 0;
        var rowIndexes = ImmutableHashSet<int>.Empty;
        var modeValue = mode.GetString()!;
        var costBasisValue = costBasis.GetString()!;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !HasExactPropertySet(item, ItemKeys) ||
                !item.TryGetProperty("ndc", out var ndc) ||
                ndc.ValueKind != JsonValueKind.String ||
                !PricingResultContentPolicy.IsExactCanonicalNdc(ndc.GetString()) ||
                !item.TryGetProperty("found", out var found) ||
                found.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                item.TryGetProperty("warning", out _) ||
                !item.TryGetProperty("supplierName", out var supplierName) ||
                !IsPersistedSupplierNameCloudSafe(supplierName) ||
                !item.TryGetProperty("rowIndex", out var rowIndex) ||
                !IsPersistedRowIndexCloudSafe(rowIndex) ||
                rowIndexes.Contains(rowIndex.GetInt32()) ||
                !item.TryGetProperty("status", out var itemStatus) ||
                itemStatus.ValueKind != JsonValueKind.String ||
                itemStatus.GetString() !=
                    (found.ValueKind == JsonValueKind.True ? "found" : "not_found") ||
                !item.TryGetProperty("confidence", out var confidence) ||
                confidence.ValueKind != JsonValueKind.Number ||
                !confidence.TryGetDecimal(out var confidenceValue) ||
                confidenceValue is < 0 or > 1 ||
                !item.TryGetProperty("source", out var source) ||
                source.ValueKind != JsonValueKind.String ||
                source.GetString() != modeValue ||
                !item.TryGetProperty("localEvidenceId", out var evidenceId) ||
                evidenceId.ValueKind != JsonValueKind.String ||
                evidenceId.GetString() !=
                    $"pricing:{expectedJobId}:{rowIndex.GetInt32()}" ||
                !item.TryGetProperty("selectorObservations", out var observations) ||
                !IsPersistedSelectorEvidenceCloudSafe(
                    observations, ref observationCount) ||
                !IsPersistedCostTupleCloudSafe(
                    item,
                    costBasisValue,
                    found.ValueKind == JsonValueKind.True))
                return false;
            rowIndexes = rowIndexes.Add(rowIndex.GetInt32());
            if (found.ValueKind == JsonValueKind.True) foundItems++;
            else notFoundItems++;
        }
        return totalItems == expectedItemCount + omittedInvalidItems &&
            completedItems == foundItems &&
            failedItems == notFoundItems + omittedInvalidItems &&
            completedItems + failedItems == totalItems &&
            observationCount + (long)omittedSelectorObservations <=
                PricingSelectorObservationPolicy.MaximumTotalObservations;
    }

    internal static bool TryReadPersistedAuthorityBinding(
        JsonElement payload,
        out string approvalId,
        out string grantDigest)
    {
        approvalId = string.Empty;
        grantDigest = string.Empty;
        if (!payload.TryGetProperty("approvalId", out var approval) ||
            approval.ValueKind != JsonValueKind.String ||
            approval.GetString() is not { Length: 36 } approvalValue ||
            !Guid.TryParseExact(approvalValue, "D", out var parsed) ||
            !string.Equals(
                approvalValue,
                parsed.ToString("D"),
                StringComparison.Ordinal) ||
            approvalValue[14] != '4' ||
            approvalValue[19] is not ('8' or '9' or 'a' or 'b') ||
            !payload.TryGetProperty("grantDigest", out var grant) ||
            grant.ValueKind != JsonValueKind.String ||
            grant.GetString() is not { Length: 64 } grantValue ||
            grantValue.Any(value => value is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
            return false;
        approvalId = approvalValue;
        grantDigest = grantValue;
        return true;
    }

    private static bool HasExactPropertySet(
        JsonElement element,
        IReadOnlySet<string> expected)
    {
        var names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Count &&
            names.Distinct(StringComparer.Ordinal).Count() == expected.Count &&
            names.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    private static bool IsPersistedSelectorEvidenceCloudSafe(
        JsonElement observations,
        ref int totalCount)
    {
        if (observations.ValueKind == JsonValueKind.Null) return true;
        if (observations.ValueKind != JsonValueKind.Array) return false;
        totalCount += observations.GetArrayLength();
        if (totalCount >
            PricingSelectorObservationPolicy.MaximumIncludedCloudObservations)
            return false;
        foreach (var observation in observations.EnumerateArray())
        {
            if (observation.ValueKind != JsonValueKind.Object ||
                !HasExactPropertySet(observation, ObservationKeys) ||
                !HasAllowedString(observation, "stepId", SelectorSteps) ||
                !HasAllowedString(observation, "resolvedVia", SelectorResolvedVia) ||
                !HasAllowedString(observation, "outcome", SelectorOutcomes) ||
                !HasAllowedString(observation, "failureKind", SelectorFailureKinds) ||
                !observation.TryGetProperty("attempted", out var attempted) ||
                attempted.ValueKind != JsonValueKind.Null &&
                    !IsPersistedElementCloudSafe(attempted) ||
                !observation.TryGetProperty(
                    "observedCandidates", out var candidates) ||
                candidates.ValueKind != JsonValueKind.Array ||
                candidates.GetArrayLength() > 128 ||
                candidates.EnumerateArray().Any(
                    candidate => !IsPersistedElementCloudSafe(candidate)))
                return false;
        }
        return true;
    }

    private static bool HasAllowedString(
        JsonElement element,
        string propertyName,
        IReadOnlySet<string> allowed) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        allowed.Contains(value.GetString() ?? "");

    private static bool IsPersistedElementCloudSafe(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !HasExactPropertySet(element, ElementKeys) ||
            !element.TryGetProperty("controlType", out var controlType) ||
            controlType.ValueKind != JsonValueKind.String ||
            !ControlTypePattern.IsMatch(controlType.GetString() ?? ""))
            return false;
        return IsOptionalStructuralIdentifier(element, "automationId") &&
            IsOptionalStructuralIdentifier(element, "className");
    }

    private static bool IsOptionalStructuralIdentifier(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return false;
        return value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.String &&
            IsCloudSafeStructuralIdentifier(value.GetString() ?? "");
    }

    private static bool TryGetPersistedMetric(
        JsonElement payload,
        string name,
        out int value) =>
        TryGetBoundedInt(
            payload,
            name,
            PricingResultPayloadBudget.MaximumSerializedMetric,
            out value);

    private static bool TryGetBoundedInt(
        JsonElement payload,
        string name,
        int maximum,
        out int value)
    {
        value = 0;
        return payload.TryGetProperty(name, out var metric) &&
            metric.ValueKind == JsonValueKind.Number &&
            metric.TryGetInt32(out value) &&
            value >= 0 && value <= maximum;
    }

    private static bool IsPersistedCostTupleCloudSafe(
        JsonElement item,
        string costBasis,
        bool found)
    {
        if (!TryGetOptionalDecimal(
                item, "costPerUnit", PricingResultContentPolicy.MaximumUnitCost,
                out var sourced) ||
            !TryGetOptionalDecimal(
                item, "baselineCostPerUnit",
                PricingResultContentPolicy.MaximumUnitCost, out var baseline) ||
            !TryGetOptionalDecimal(
                item, "quantity", PricingResultContentPolicy.MaximumQuantity,
                out var quantity) ||
            !TryGetOptionalDecimal(
                item, "packageCost", PricingResultContentPolicy.MaximumUnitCost,
                out var package))
            return false;
        if (costBasis == PricingApprovalContract.PackageCostBasis)
            return (found ? package is not null : package is null) &&
                sourced is null && baseline is null && quantity is null;
        if (costBasis != PricingApprovalContract.CostPerUnitBasis ||
            package is not null)
            return false;
        if (baseline is null || sourced is null || quantity is null || baseline <= sourced)
            return true;
        try
        {
            return (baseline.Value - sourced.Value) * quantity.Value <=
                PricingResultContentPolicy.MaximumSavingsTotal;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryGetOptionalDecimal(
        JsonElement item,
        string name,
        decimal maximum,
        out decimal? value)
    {
        value = null;
        if (!item.TryGetProperty(name, out var element) ||
            element.ValueKind == JsonValueKind.Null)
            return true;
        if (element.ValueKind != JsonValueKind.Number ||
            !element.TryGetDecimal(out var parsed) || parsed < 0 || parsed > maximum)
            return false;
        value = parsed;
        return true;
    }

    private static bool IsPersistedSupplierNameCloudSafe(JsonElement supplierName) =>
        supplierName.ValueKind == JsonValueKind.Null ||
        supplierName.ValueKind == JsonValueKind.String &&
        PricingResultContentPolicy.IsCloudSafeSupplierName(supplierName.GetString());

    private static bool IsPersistedRowIndexCloudSafe(JsonElement rowIndex) =>
        rowIndex.ValueKind == JsonValueKind.Number &&
        rowIndex.TryGetInt32(out var value) &&
        value is >= 0 and <= 1_000_000;
}
