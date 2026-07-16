using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

internal sealed record ExpectedPricingRow(
    int RowIndex,
    string? CanonicalNdc,
    bool IsInvalid);

internal sealed record PricingInputManifest(
    string RowFingerprint,
    IReadOnlyDictionary<int, ExpectedPricingRow> Rows)
{
    internal int Count => Rows.Count;
}

/// <summary>
/// Exact identity and completion invariants for one admitted pricing run.
/// Nothing from Helper, SQL, or prior SQLite state is trusted by row index alone.
/// </summary>
internal static class PricingRunIntegrity
{
    private const string FingerprintVersion = "suavo-pricing-input-v1\n";

    internal static bool TryCreateManifest(
        ReadResult input,
        out PricingInputManifest manifest)
    {
        manifest = null!;
        var expected = new Dictionary<int, ExpectedPricingRow>();
        foreach (var row in input.Rows)
        {
            if (row.RowIndex < 1 ||
                !PricingResultContentPolicy.IsExactCanonicalNdc(row.NdcNormalized) ||
                !expected.TryAdd(row.RowIndex, new ExpectedPricingRow(
                    row.RowIndex, row.NdcNormalized, IsInvalid: false)))
                return false;
        }
        foreach (var row in input.Invalid)
        {
            if (row.RowIndex < 1 ||
                !expected.TryAdd(row.RowIndex, new ExpectedPricingRow(
                    row.RowIndex, CanonicalNdc: null, IsInvalid: true)))
                return false;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(FingerprintVersion));
        foreach (var row in expected.Values.OrderBy(item => item.RowIndex))
        {
            var line = string.Concat(
                row.RowIndex.ToString(CultureInfo.InvariantCulture),
                ":",
                row.IsInvalid ? "I" : "V",
                ":",
                row.CanonicalNdc ?? "",
                "\n");
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }
        var fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        manifest = new PricingInputManifest(
            fingerprint,
            new ReadOnlyDictionary<int, ExpectedPricingRow>(expected));
        return true;
    }

    internal static bool TryValidateLookupResult(
        string jobId,
        NdcRow expected,
        SupplierPriceResult actual,
        out string code) => TryValidateLookupResult(
            jobId,
            expected,
            actual,
            PricingApprovalContract.CostPerUnitBasis,
            out code);

    internal static bool TryValidateLookupResult(
        string jobId,
        NdcRow expected,
        SupplierPriceResult actual,
        string costBasis,
        out string code)
    {
        if (!string.Equals(actual.JobId, jobId, StringComparison.Ordinal) ||
            actual.RowIndex != expected.RowIndex ||
            !string.Equals(actual.Ndc, expected.NdcNormalized, StringComparison.Ordinal))
        {
            code = "pricing_result_identity_mismatch";
            return false;
        }
        if (!HasValidOutcomeShape(actual, costBasis))
        {
            code = "pricing_result_outcome_invalid";
            return false;
        }
        code = "ok";
        return true;
    }

    internal static bool TryValidatePersistedResults(
        string jobId,
        PricingInputManifest manifest,
        IReadOnlyList<SupplierPriceResult> results,
        out string code) => TryValidatePersistedResults(
            jobId,
            manifest,
            results,
            PricingApprovalContract.CostPerUnitBasis,
            out code);

    internal static bool TryValidatePersistedResults(
        string jobId,
        PricingInputManifest manifest,
        IReadOnlyList<SupplierPriceResult> results,
        string costBasis,
        out string code)
    {
        var seen = new HashSet<int>();
        foreach (var result in results)
        {
            if (!string.Equals(result.JobId, jobId, StringComparison.Ordinal) ||
                !seen.Add(result.RowIndex) ||
                !manifest.Rows.TryGetValue(result.RowIndex, out var expected))
            {
                code = "pricing_persisted_result_identity_invalid";
                return false;
            }

            if (expected.IsInvalid)
            {
                if (!IsExactInvalidRow(result, costBasis))
                {
                    code = "pricing_persisted_invalid_row_shape_invalid";
                    return false;
                }
            }
            else if (!string.Equals(
                         result.Ndc, expected.CanonicalNdc, StringComparison.Ordinal) ||
                     !HasValidOutcomeShape(result, costBasis))
            {
                code = "pricing_persisted_result_outcome_invalid";
                return false;
            }
        }
        code = "ok";
        return true;
    }

    internal static bool IsTerminallyComplete(
        string jobId,
        PricingInputManifest manifest,
        IReadOnlyList<SupplierPriceResult> results,
        WriteResult write,
        int completed,
        int failed)
        => IsTerminallyComplete(
            jobId,
            manifest,
            results,
            write,
            completed,
            failed,
            PricingApprovalContract.CostPerUnitBasis);

    internal static bool IsTerminallyComplete(
        string jobId,
        PricingInputManifest manifest,
        IReadOnlyList<SupplierPriceResult> results,
        WriteResult write,
        int completed,
        int failed,
        string costBasis)
    {
        return manifest.Count > 0 &&
               results.Count == manifest.Count &&
               completed == manifest.Count &&
               failed == 0 &&
               write.Success &&
               write.OkRows == manifest.Count &&
               write.FailRows == 0 &&
               manifest.Rows.Values.All(row => !row.IsInvalid) &&
               TryValidatePersistedResults(
                   jobId, manifest, results, costBasis, out _) &&
               results.All(result => result.Found);
    }

    internal static bool IsTerminallyReviewComplete(
        string jobId,
        PricingInputManifest manifest,
        IReadOnlyList<SupplierPriceResult> results,
        WriteResult write,
        int completed,
        int failed,
        string costBasis)
    {
        return costBasis == PricingApprovalContract.PackageCostBasis &&
               manifest.Count > 0 &&
               results.Count == manifest.Count &&
               completed + failed == manifest.Count &&
               write.Success &&
               write.OkRows == completed &&
               write.FailRows == failed &&
               TryValidatePersistedResults(
                   jobId, manifest, results, costBasis, out _);
    }

    private static bool HasValidOutcomeShape(
        SupplierPriceResult result,
        string costBasis)
    {
        var observationTotal =
            (long)result.OmittedSelectorObservations +
            (result.Observations?.Count ?? 0);
        if (result.OmittedSelectorObservations < 0 ||
            observationTotal > PricingSelectorObservationPolicy.MaximumTotalObservations)
            return false;

        if (!string.Equals(result.CostBasis, costBasis, StringComparison.Ordinal))
            return false;

        if (result.Found)
            return !string.IsNullOrWhiteSpace(result.SupplierName) &&
                   string.IsNullOrWhiteSpace(result.ErrorMessage) &&
                   (costBasis == PricingApprovalContract.CostPerUnitBasis
                       ? IsExactUnitCost(result.CostPerUnit) &&
                         result.PackageCost is null &&
                         IsOptionalExactUnitCost(result.BaselineCostPerUnit) &&
                         IsOptionalExactQuantity(result.Quantity) &&
                         SavingsTupleFitsPersistence(result)
                       : costBasis == PricingApprovalContract.PackageCostBasis &&
                         result.CostPerUnit is null &&
                         IsExactUnitCost(result.PackageCost) &&
                         result.BaselineCostPerUnit is null &&
                         result.Quantity is null);

        return string.IsNullOrWhiteSpace(result.SupplierName) &&
               result.CostPerUnit is null &&
               result.PackageCost is null &&
               !string.IsNullOrWhiteSpace(result.ErrorMessage) &&
               result.BaselineCostPerUnit is null &&
               result.Quantity is null;
    }

    private static bool IsExactInvalidRow(
        SupplierPriceResult result,
        string costBasis) =>
        !result.Found &&
        result.CostBasis == costBasis &&
        string.Equals(
            result.Ndc,
            PricingResultContentPolicy.InvalidNdcStorageValue,
            StringComparison.Ordinal) &&
        string.IsNullOrWhiteSpace(result.SupplierName) &&
        result.CostPerUnit is null &&
        result.PackageCost is null &&
        result.ErrorMessage == PricingResultContentPolicy.InvalidNdcReasonCode &&
        result.BaselineCostPerUnit is null &&
        result.Quantity is null;

    private static bool IsExactUnitCost(decimal? value) =>
        value is > 0 and <= PricingResultContentPolicy.MaximumUnitCost &&
        decimal.Round(
            value.Value, 4, MidpointRounding.AwayFromZero) == value.Value;

    private static bool IsOptionalExactUnitCost(decimal? value) =>
        value is null || IsExactUnitCost(value);

    private static bool IsOptionalExactQuantity(decimal? value) =>
        value is null ||
        value is > 0 and <= PricingResultContentPolicy.MaximumQuantity &&
        decimal.Round(
            value.Value, 3, MidpointRounding.AwayFromZero) == value.Value;

    private static bool SavingsTupleFitsPersistence(SupplierPriceResult result)
    {
        if (result.BaselineCostPerUnit is not { } baseline ||
            result.CostPerUnit is not { } sourced ||
            result.Quantity is not { } quantity ||
            baseline <= sourced)
            return true;
        try
        {
            return (baseline - sourced) * quantity <=
                   PricingResultContentPolicy.MaximumSavingsTotal;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
