using System.Globalization;
using ClosedXML.Excel;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// In-memory, read-only Feature-B source created only from an admitted private workbook snapshot.
/// The loader is deliberately internal: production composition must enter through
/// <see cref="PreferredNdcWorkbookAdmission"/>, which applies the archive/DLP boundary before this
/// exact schema parser sees any values.
/// </summary>
public sealed class ExcelPreferredNdcReader : IPreferredNdcDataSource
{
    internal const string RequiredWorksheetName = "Preferred NDC Candidates";
    internal const int MaximumDataRows = PreferredNdcEvidencePolicy.MaximumCandidatesPerWorkbook;

    internal static readonly string[] RequiredHeaders =
    [
        "Drug Group Key",
        "Insurance Plan ID",
        "NDC11",
        "Manufacturer",
        "Acquisition Amount",
        "Acquisition Amount Basis",
        "Expected Reimbursement",
        "Reimbursement Amount Basis",
        "Available",
        "Eligible",
        "Reimbursement Basis",
        "Acquisition Evidence Provenance",
        "Reimbursement Evidence Provenance",
        "Acquisition Evidence As Of UTC",
        "Reimbursement Evidence As Of UTC",
        "Historical Sample Count",
    ];

    private readonly IReadOnlyDictionary<PairKey, PairBucket> _byPair;

    private ExcelPreferredNdcReader(
        IReadOnlyDictionary<PairKey, PairBucket> byPair,
        IReadOnlyList<(string DrugGroupKey, string PlanId)> pairs)
    {
        _byPair = byPair;
        Pairs = pairs;
    }

    /// <summary>The exact, first-seen order of admitted medication/plan pairs.</summary>
    public IReadOnlyList<(string DrugGroupKey, string PlanId)> Pairs { get; }

    public Task<PreferredNdcReadResult> ReadCandidatesAsync(
        PreferredNdcRequest request,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = new PairKey(request.DrugGroupKey, request.PlanId);
        if (_byPair.TryGetValue(key, out var hit))
        {
            return Task.FromResult(new PreferredNdcReadResult(
                request.JobId,
                request.RowIndex,
                request.DrugGroupKey,
                request.PlanId,
                Found: true,
                hit.Candidates,
                hit.Basis,
                ErrorMessage: null));
        }

        return Task.FromResult(new PreferredNdcReadResult(
            request.JobId,
            request.RowIndex,
            request.DrugGroupKey,
            request.PlanId,
            Found: false,
            Array.Empty<PreferredNdcCandidate>(),
            ReimbursementBasis.Unspecified,
            "pair_not_in_sheet"));
    }

    internal static ExcelPreferredNdcReader LoadAdmittedSnapshot(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using var workbook = new XLWorkbook(stream);
            if (workbook.Worksheets.Count != 1)
                throw SchemaError();

            var worksheet = workbook.Worksheets.Single();
            if (!string.Equals(
                    worksheet.Name,
                    RequiredWorksheetName,
                    StringComparison.Ordinal))
                throw SchemaError();

            var lastRow = worksheet.LastRowUsed()?.RowNumber() ?? 0;
            var lastColumn = worksheet.LastColumnUsed()?.ColumnNumber() ?? 0;
            if (lastRow < 2 || lastRow - 1 > MaximumDataRows ||
                lastColumn != RequiredHeaders.Length)
                throw SchemaError();

            var columns = ReadExactHeaderMap(worksheet, lastColumn);
            var builders = new Dictionary<PairKey, PairBuilder>();
            var pairOrder = new List<(string DrugGroupKey, string PlanId)>();

            for (var row = 2; row <= lastRow; row++)
            {
                if (IsBlankRow(worksheet, row, lastColumn))
                    continue;

                var drug = ReadIdentity(worksheet.Cell(row, columns["Drug Group Key"]));
                var plan = ReadIdentity(worksheet.Cell(row, columns["Insurance Plan ID"]));
                var ndc = ReadIdentity(worksheet.Cell(row, columns["NDC11"]));
                if (!PreferredNdcEvidencePolicy.IsCanonicalNdc11(ndc))
                    throw DataError();

                var key = new PairKey(drug, plan);
                if (!builders.TryGetValue(key, out var builder))
                {
                    builder = new PairBuilder();
                    builders.Add(key, builder);
                    pairOrder.Add((drug, plan));
                }
                if (!builder.NdcIdentities.Add(ndc))
                    throw new PricingWorkbookContentException("xlsx_preferred_ndc_duplicate_identity");

                var basis = ReadReimbursementBasis(
                    worksheet.Cell(row, columns["Reimbursement Basis"]));
                if (builder.Basis is { } existingBasis && existingBasis != basis)
                    throw DataError();
                builder.Basis = basis;

                builder.Candidates.Add(new PreferredNdcCandidate(
                    ndc,
                    ReadOptionalText(worksheet.Cell(row, columns["Manufacturer"]), 200),
                    ReadNullableAmount(worksheet.Cell(row, columns["Acquisition Amount"])),
                    ReadNullableAmount(worksheet.Cell(row, columns["Expected Reimbursement"])),
                    ReadBoolean(worksheet.Cell(row, columns["Available"])),
                    ReadBoolean(worksheet.Cell(row, columns["Eligible"])),
                    ReadAmountBasis(worksheet.Cell(row, columns["Acquisition Amount Basis"])),
                    ReadAmountBasis(worksheet.Cell(row, columns["Reimbursement Amount Basis"])),
                    ReadAcquisitionProvenance(
                        worksheet.Cell(row, columns["Acquisition Evidence Provenance"])),
                    ReadReimbursementProvenance(
                        worksheet.Cell(row, columns["Reimbursement Evidence Provenance"])),
                    ReadEvidenceTime(
                        worksheet.Cell(row, columns["Acquisition Evidence As Of UTC"])),
                    ReadEvidenceTime(
                        worksheet.Cell(row, columns["Reimbursement Evidence As Of UTC"])),
                    ReadSampleCount(worksheet.Cell(row, columns["Historical Sample Count"]))));
            }

            if (builders.Count == 0)
                throw SchemaError();

            var frozen = builders.ToDictionary(
                pair => pair.Key,
                pair => new PairBucket(
                    pair.Value.Candidates.ToArray(),
                    pair.Value.Basis ?? ReimbursementBasis.Unspecified));
            return new ExcelPreferredNdcReader(frozen, pairOrder.ToArray());
        }
        catch (PricingWorkbookContentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is
            IOException or InvalidDataException or ArgumentException or FormatException or OverflowException)
        {
            throw new PricingWorkbookContentException("xlsx_preferred_ndc_schema_invalid");
        }
    }

    private static Dictionary<string, int> ReadExactHeaderMap(
        IXLWorksheet worksheet,
        int lastColumn)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var column = 1; column <= lastColumn; column++)
        {
            var header = worksheet.Cell(1, column).GetString();
            if (!columns.TryAdd(header, column))
                throw SchemaError();
        }

        if (columns.Count != RequiredHeaders.Length ||
            RequiredHeaders.Any(header => !columns.ContainsKey(header)))
            throw SchemaError();
        return columns;
    }

    private static bool IsBlankRow(IXLWorksheet worksheet, int row, int lastColumn)
    {
        for (var column = 1; column <= lastColumn; column++)
        {
            if (!string.IsNullOrWhiteSpace(worksheet.Cell(row, column).GetString()))
                return false;
        }
        return true;
    }

    private static string ReadIdentity(IXLCell cell)
    {
        var raw = cell.GetString();
        var value = raw.Trim();
        if (!string.Equals(raw, value, StringComparison.Ordinal) ||
            value.Length is < 1 or > 200 || value.Any(char.IsControl))
            throw DataError();
        return value;
    }

    private static string ReadOptionalText(IXLCell cell, int maximumLength)
    {
        var value = cell.GetString().Trim();
        if (value.Length > maximumLength || value.Any(char.IsControl))
            throw DataError();
        return value;
    }

    private static decimal? ReadNullableAmount(IXLCell cell)
    {
        var value = cell.GetString().Trim();
        if (value.Length == 0)
            return null;
        if (!decimal.TryParse(
                value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var amount))
            throw DataError();
        return amount;
    }

    private static bool ReadBoolean(IXLCell cell)
    {
        var value = cell.GetString().Trim();
        return value switch
        {
            "TRUE" => true,
            "FALSE" => false,
            _ => throw DataError(),
        };
    }

    private static PreferredNdcAmountBasis ReadAmountBasis(IXLCell cell) =>
        cell.GetString().Trim() switch
        {
            "per_dispensed_fill" => PreferredNdcAmountBasis.PerDispensedFill,
            "per_package" => PreferredNdcAmountBasis.PerPackage,
            "per_unit" => PreferredNdcAmountBasis.PerUnit,
            _ => throw DataError(),
        };

    private static ReimbursementBasis ReadReimbursementBasis(IXLCell cell) =>
        cell.GetString().Trim() switch
        {
            "contract_or_mac" => ReimbursementBasis.ContractOrMac,
            "adjudicated_history" => ReimbursementBasis.AdjudicatedHistory,
            _ => throw DataError(),
        };

    private static PreferredNdcEvidenceProvenance ReadAcquisitionProvenance(IXLCell cell) =>
        cell.GetString().Trim() switch
        {
            "pioneerrx_acquisition_cost_export" =>
                PreferredNdcEvidenceProvenance.PioneerRxAcquisitionCostExport,
            _ => throw DataError(),
        };

    private static PreferredNdcEvidenceProvenance ReadReimbursementProvenance(IXLCell cell) =>
        cell.GetString().Trim() switch
        {
            "pioneerrx_contract_or_mac_export" =>
                PreferredNdcEvidenceProvenance.PioneerRxContractOrMacExport,
            "pioneerrx_adjudicated_claims_export" =>
                PreferredNdcEvidenceProvenance.PioneerRxAdjudicatedClaimsExport,
            _ => throw DataError(),
        };

    private static DateTimeOffset ReadEvidenceTime(IXLCell cell)
    {
        var value = cell.GetString().Trim();
        if (!value.EndsWith('Z') ||
            !DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            throw DataError();
        return parsed;
    }

    private static int ReadSampleCount(IXLCell cell)
    {
        var value = cell.GetString().Trim();
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
            count is < 0 or > PreferredNdcEvidencePolicy.MaximumHistoricalSampleCount)
            throw DataError();
        return count;
    }

    private static PricingWorkbookContentException SchemaError() =>
        new("xlsx_preferred_ndc_schema_forbidden");

    private static PricingWorkbookContentException DataError() =>
        new("xlsx_preferred_ndc_data_forbidden");

    private readonly record struct PairKey(string DrugGroupKey, string PlanId);

    private sealed record PairBucket(
        IReadOnlyList<PreferredNdcCandidate> Candidates,
        ReimbursementBasis Basis);

    private sealed class PairBuilder
    {
        internal List<PreferredNdcCandidate> Candidates { get; } = [];
        internal HashSet<string> NdcIdentities { get; } = new(StringComparer.Ordinal);
        internal ReimbursementBasis? Basis { get; set; }
    }
}
