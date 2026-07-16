using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Pricing;

/// <summary>The exact semantics under which persisted pricing rows were observed.</summary>
public sealed record PricingObservationContract(
    string Modality,
    string SchemaDigest,
    string StatusPolicyDigest,
    string CostBasis,
    string PolicyDigest,
    string SnapshotContract,
    TimeSpan FreshnessWindow);

/// <summary>Exact pharmacist authority admitted for one observation contract.</summary>
public sealed record PricingCostBasisAuthority(
    string PharmacyId,
    string ApprovedByRole,
    string CostBasis,
    string PolicyDigest,
    string ApprovalId,
    string ApprovalDigest,
    DateTimeOffset ExpiresAtUtc);

public static class PricingObservationPolicy
{
    public const int PricingApprovalSchemaVersion = PricingApprovalContract.SchemaVersion;
    public const int PackagePricingApprovalSchemaVersion =
        PricingApprovalContract.PackageSchemaVersion;
    public const string CostPerUnitBasis = PricingApprovalContract.CostPerUnitBasis;
    public const string PackageCostBasis = PricingApprovalContract.PackageCostBasis;
    public const string PharmacistInChargeRole =
        PricingApprovalContract.PharmacistInChargeRole;
    public const string SnapshotContractV1 = PricingApprovalContract.SnapshotContractV1;
    public const string PackageSnapshotContractV2 =
        PricingApprovalContract.PackageSnapshotContractV2;
    public static readonly TimeSpan DefaultFreshnessWindow = TimeSpan.FromHours(12);

    public static PricingObservationContract CreateUia(string modality)
        => CreateUia(
            modality,
            new string('0', 64),
            new string('0', 64),
            Array.Empty<SelectorPatch>());

    internal static PricingObservationContract CreateUia(
        string modality,
        string pmsFingerprint,
        string screenSignature,
        IReadOnlyList<SelectorPatch> activePatches) => CreateUia(
            modality,
            pmsFingerprint,
            screenSignature,
            activePatches,
            CostPerUnitBasis);

    internal static PricingObservationContract CreateUia(
        string modality,
        string pmsFingerprint,
        string screenSignature,
        IReadOnlyList<SelectorPatch> activePatches,
        string costBasis)
    {
        if (modality is not ("uia" or "vision"))
            throw new ArgumentOutOfRangeException(nameof(modality));
        if (!IsLowerHex64(pmsFingerprint) || !IsLowerHex64(screenSignature))
            throw new ArgumentException("Pricing live screen identity is invalid.");
        if (!PricingApprovalContract.IsSupportedCostBasis(costBasis))
            throw new ArgumentException("Pricing cost basis is invalid.");
        if (costBasis == PackageCostBasis && modality != "uia")
            throw new ArgumentException("Package-cost pricing requires UI Automation.");
        var selectorDigest = SelectorSnapshotDigest(activePatches);
        var schema = costBasis == PackageCostBasis
            ? Digest(
                "pioneerrx_supplier_catalog_uia_v3",
                modality,
                "quick_search:help_text_or_pic_approved_selector",
                "quick_search_selection:two_enter_exact_ndc_non_do_not_use",
                "filters:include_discontinued_no_inventory_group_rx",
                "cost_column:exact_cost_header_uia_cell",
                pmsFingerprint,
                screenSignature,
                selectorDigest)
            : Digest(
                "pioneerrx_supplier_catalog_uia_v3",
                modality,
                "quick_search:help_text_or_pic_approved_selector",
                "cost_column:exact_cost_per_unit_header_or_uia_cell",
                pmsFingerprint,
                screenSignature,
                selectorDigest);
        var status = costBasis == PackageCostBasis
            ? Digest(
                "eligible_status_v2",
                "Available",
                "Active",
                "linked:true",
                "inventory_group:Rx",
                "discontinued:false",
                "include_discontinued_filter:No",
                "inventory_group_filter:Rx")
            : Digest("eligible_status_v1", "Available", "Active");
        return Create(modality, schema, status, costBasis);
    }

    internal static string SelectorSnapshotDigest(
        IReadOnlyList<SelectorPatch> activePatches)
    {
        ArgumentNullException.ThrowIfNull(activePatches);
        var values = new List<string>
        {
            "pricing_selector_snapshot_v1",
            activePatches.Count.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var patch in activePatches
                     .OrderBy(value => value.StepId)
                     .ThenBy(value => value.Version)
                     .ThenBy(value => value.PatchId, StringComparer.Ordinal))
        {
            values.Add(patch.PatchId);
            values.Add(patch.SkillId);
            values.Add(patch.StepId.ToString());
            values.Add(patch.PmsFingerprint ?? "");
            values.Add(patch.ScreenSignatureV1 ?? "");
            values.Add(patch.Target.CanonicalRepr);
            values.Add(patch.Fallbacks.Count.ToString(CultureInfo.InvariantCulture));
            values.AddRange(patch.Fallbacks.Select(value => value.CanonicalRepr));
            values.Add(patch.Confidence.ToString("R", CultureInfo.InvariantCulture));
            values.Add(patch.SeedDigest);
            values.Add(patch.Version.ToString(CultureInfo.InvariantCulture));
            values.Add(patch.ApprovedByRole ?? "");
        }
        return Digest(values.ToArray());
    }

    public static PricingObservationContract CreateSql(DiscoveredPricingSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var schemaDigest = Digest(
            "pioneerrx_supplier_catalog_sql_v2",
            schema.CatalogSchema,
            schema.CatalogTable,
            schema.CostColumn,
            Shape(schema.CostColumnShape),
            schema.CostPerUnitColumn ?? "",
            Shape(schema.CostPerUnitColumnShape),
            schema.NdcColumn ?? "",
            Shape(schema.NdcColumnShape ?? schema.ItemJoin?.NdcColumnShape),
            schema.ItemJoin?.ItemTableSchema ?? "",
            schema.ItemJoin?.ItemTable ?? "",
            schema.ItemJoin?.ItemIdColumnInCatalog ?? "",
            schema.ItemJoin?.ItemIdColumnInItem ?? "",
            schema.ItemJoin?.NdcColumnInItem ?? "",
            schema.SupplierSource.Resolution.ToString(),
            schema.SupplierSource.NameColumnInCatalog ?? "",
            schema.SupplierSource.SupplierTableSchema ?? "",
            schema.SupplierSource.SupplierTable ?? "",
            schema.SupplierSource.SupplierIdColumnInCatalog ?? "",
            schema.SupplierSource.SupplierIdColumnInSupplier ?? "",
            schema.SupplierSource.SupplierNameColumnInSupplier ?? "",
            schema.StatusColumn ?? "",
            Shape(schema.StatusColumnShape));
        var statuses = schema.AvailableStatusValues
            .Select(value => value.ToUpperInvariant())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var statusDigest = Digest(["eligible_status_v1", .. statuses]);
        return Create("sql", schemaDigest, statusDigest, CostPerUnitBasis);
    }

    public static PricingCostBasisAuthority? TryAdmitAuthority(
        PricingApprovalGrant? approval,
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        PricingObservationContract contract,
        DateTimeOffset now,
        out string code) => TryAdmitAuthority(
            approval,
            pharmacyId,
            agentId,
            machineFingerprint,
            contract,
            now,
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            out code);

    internal static PricingCostBasisAuthority? TryAdmitAuthority(
        PricingApprovalGrant? approval,
        string pharmacyId,
        string agentId,
        string machineFingerprint,
        PricingObservationContract contract,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        out string code)
    {
        if (approval is null)
        {
            code = "pricing_cost_basis_approval_required";
            return null;
        }
        if (!PricingApprovalContract.IsValidGrant(
                approval,
                now,
                trustedPublicKeys,
                out code))
            return null;

        if (!string.Equals(approval.PharmacyId, pharmacyId, StringComparison.Ordinal) ||
            !string.Equals(approval.AgentId, agentId, StringComparison.Ordinal) ||
            !string.Equals(
                approval.MachineFingerprint,
                machineFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(approval.Modality, contract.Modality, StringComparison.Ordinal) ||
            !string.Equals(approval.SchemaDigest, contract.SchemaDigest, StringComparison.Ordinal) ||
            !string.Equals(
                approval.StatusPolicyDigest,
                contract.StatusPolicyDigest,
                StringComparison.Ordinal) ||
            !string.Equals(approval.CostBasis, contract.CostBasis, StringComparison.Ordinal) ||
            !string.Equals(approval.PolicyDigest, contract.PolicyDigest, StringComparison.Ordinal) ||
            !string.Equals(
                approval.SnapshotContract,
                contract.SnapshotContract,
                StringComparison.Ordinal) ||
            approval.FreshnessSeconds != (long)contract.FreshnessWindow.TotalSeconds)
        {
            code = "pricing_cost_basis_approval_required";
            return null;
        }

        var digest = PricingApprovalContract.ComputeGrantDigest(approval);
        code = "pricing_cost_basis_approval_admitted";
        return new PricingCostBasisAuthority(
            pharmacyId,
            approval.ApprovedByRole,
            approval.CostBasis,
            approval.PolicyDigest,
            approval.ApprovalId,
            digest,
            approval.ExpiresAtUtc.ToUniversalTime());
    }

    private static bool IsLowerHex64(string value) =>
        value.Length == 64 &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static bool TryMatchJobAuthority(
        PricingJobSpec spec,
        PricingCostBasisAuthority authority,
        out string code)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(authority);
        if (spec.ApprovalId is null && spec.GrantDigest is null)
        {
            code = "pricing_job_authority_active";
            return true;
        }
        if (spec.ApprovalId is not { Length: 36 } approvalId ||
            !Guid.TryParseExact(approvalId, "D", out var parsed) ||
            !string.Equals(approvalId, parsed.ToString("D"), StringComparison.Ordinal) ||
            approvalId[14] != '4' || approvalId[19] is not ('8' or '9' or 'a' or 'b') ||
            spec.GrantDigest is not { } grantDigest ||
            !IsLowerHex64(grantDigest) ||
            !string.Equals(approvalId, authority.ApprovalId, StringComparison.Ordinal) ||
            !IsLowerHex64(authority.ApprovalDigest) ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(grantDigest),
                Convert.FromHexString(authority.ApprovalDigest)))
        {
            code = "pricing_job_authority_binding_invalid";
            return false;
        }

        code = "pricing_job_authority_active";
        return true;
    }

    private static PricingObservationContract Create(
        string modality,
        string schemaDigest,
        string statusPolicyDigest,
        string costBasis)
    {
        var snapshotContract = PricingApprovalContract
            .SnapshotContractForCostBasis(costBasis);
        var policyDigest = PricingApprovalContract.ComputeObservationPolicyDigest(
            modality,
            schemaDigest,
            statusPolicyDigest,
            costBasis,
            snapshotContract,
            (long)DefaultFreshnessWindow.TotalSeconds);
        return new PricingObservationContract(
            modality,
            schemaDigest,
            statusPolicyDigest,
            costBasis,
            policyDigest,
            snapshotContract,
            DefaultFreshnessWindow);
    }

    private static string Shape(PricingSqlColumnShape? shape) => shape is null
        ? ""
        : string.Join(':',
            shape.DataType.ToLowerInvariant(),
            shape.MaxLength?.ToString(CultureInfo.InvariantCulture) ?? "",
            shape.Precision?.ToString(CultureInfo.InvariantCulture) ?? "",
            shape.Scale?.ToString(CultureInfo.InvariantCulture) ?? "",
            shape.IsNullable ? "1" : "0");

    internal static string Digest(params string[] values)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? "");
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
