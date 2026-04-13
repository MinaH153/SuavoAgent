using System.Text.Json;
using SuavoAgent.Contracts.Canary;

namespace SuavoAgent.Core.Canary;

/// Pure severity classifier for schema drift detection.
/// Compares a ContractBaseline (approved hashes + serialized objects) against
/// an ObservedContract (live schema metadata) and returns the highest severity found.
public static class SchemaCanaryClassifier
{
    public static ContractVerification Classify(ContractBaseline baseline, ObservedContract observed)
    {
        var drifted = new List<string>();
        var severity = CanarySeverity.None;

        // ── 1. Status map ───────────────────────────────────────────────────
        var observedStatusHash = ContractFingerprinter.HashStatusMap(observed.StatusMap);
        if (observedStatusHash != baseline.StatusMapFingerprint)
        {
            Escalate(ref severity, CanarySeverity.Critical);
            drifted.Add("status_map");
        }
        else
        {
            // Duplicate description check (same hash won't catch this — different GUIDs collapse)
            var descGroups = observed.StatusMap
                .GroupBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);
            if (descGroups.Any())
            {
                Escalate(ref severity, CanarySeverity.Critical);
                drifted.Add("status_map");
            }
        }

        // Check for duplicate descriptions even when hash differs (cover the dup+hash-mismatch case)
        if (!drifted.Contains("status_map"))
        {
            var dups = observed.StatusMap
                .GroupBy(s => s.Description, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() > 1);
            if (dups)
            {
                Escalate(ref severity, CanarySeverity.Critical);
                drifted.Add("status_map");
            }
        }

        // ── 2. Query fingerprint ────────────────────────────────────────────
        if (observed.QueryFingerprint != baseline.QueryFingerprint)
        {
            Escalate(ref severity, CanarySeverity.Critical);
            drifted.Add("query");
        }

        // ── 3. Result shape fingerprint ─────────────────────────────────────
        if (observed.ResultShapeFingerprint != baseline.ResultShapeFingerprint)
        {
            Escalate(ref severity, CanarySeverity.Critical);
            drifted.Add("result_shape");
        }

        // ── 4. Object fingerprint ───────────────────────────────────────────
        var observedObjHash = ContractFingerprinter.HashObjects(observed.Objects);
        if (observedObjHash != baseline.ObjectFingerprint)
        {
            var (objSeverity, component) = ClassifyObjectDrift(baseline, observed.Objects);
            Escalate(ref severity, objSeverity);
            drifted.Add(component);
        }

        // Also check for duplicate descriptions when hash already changed (they're on the same drifted)
        // (already handled above via the initial status_map check)

        var isValid = severity == CanarySeverity.None;
        return new ContractVerification(isValid, severity, drifted.AsReadOnly(),
            baseline.ContractFingerprint, null, null);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Object drift grading
    // ─────────────────────────────────────────────────────────────────────────

    private static (CanarySeverity Severity, string Component) ClassifyObjectDrift(
        ContractBaseline baseline, IReadOnlyList<ObservedObject> observedObjects)
    {
        // Deserialize the baseline objects from ContractJson.
        // ContractJson stores the serialized IReadOnlyList<ObservedObject> written at baseline creation.
        IReadOnlyList<ObservedObject>? baselineObjects = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(baseline.ContractJson) && baseline.ContractJson != "{}")
                baselineObjects = JsonSerializer.Deserialize<List<ObservedObject>>(baseline.ContractJson);
        }
        catch
        {
            // Fall through to Critical if we can't parse
        }

        if (baselineObjects == null || baselineObjects.Count == 0)
            return (CanarySeverity.Critical, "object");

        // Build lookup: "schema.table.column" → ObservedObject for both sides
        static string ObjectKey(ObservedObject o) =>
            $"{o.SchemaName}.{o.TableName}.{o.ColumnName}".ToLowerInvariant();

        var baselineMap = baselineObjects.ToDictionary(ObjectKey);
        var observedMap = observedObjects.ToDictionary(ObjectKey);

        var worstSeverity = CanarySeverity.None;

        foreach (var (key, baseObj) in baselineMap)
        {
            if (!observedMap.TryGetValue(key, out var obsObj))
            {
                // Object present in baseline but missing from observed
                var missingSeverity = baseObj.IsRequired ? CanarySeverity.Critical : CanarySeverity.Warning;
                Escalate(ref worstSeverity, missingSeverity);
                continue;
            }

            // Object present — check for type drift
            if (!string.Equals(obsObj.DataTypeName, baseObj.DataTypeName, StringComparison.OrdinalIgnoreCase))
            {
                // Different base type → incompatible → Critical
                Escalate(ref worstSeverity, CanarySeverity.Critical);
                continue;
            }

            // Same base type — check for widening (MaxLength increased)
            if (obsObj.MaxLength != baseObj.MaxLength)
            {
                Escalate(ref worstSeverity, CanarySeverity.Warning);
            }

            // Nullable flag changed
            if (obsObj.IsNullable != baseObj.IsNullable)
            {
                Escalate(ref worstSeverity, CanarySeverity.Warning);
            }
        }

        return (worstSeverity == CanarySeverity.None ? CanarySeverity.Warning : worstSeverity, "object");
    }

    private static void Escalate(ref CanarySeverity current, CanarySeverity candidate)
    {
        if (candidate > current)
            current = candidate;
    }
}
