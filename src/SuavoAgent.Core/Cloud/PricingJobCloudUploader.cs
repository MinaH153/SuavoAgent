using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Config;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Core.Cloud;

public sealed record PricingJobCloudUploadReceipt(
    bool Accepted,
    string Code,
    int Recorded,
    bool VerifiedTerminal = false);

public sealed partial class PricingJobCloudUploader
{
    private const string ContentBlockedCode = "pricing_result_outbox_content_blocked";
    private const string CommandIneligibleCode = "pricing_result_command_ineligible";
    private const string NotCompleteCode = "pricing_result_not_complete";
    private static readonly FrozenDictionary<string, FrozenSet<int>> TerminalStatuses =
        new Dictionary<string, FrozenSet<int>>(StringComparer.Ordinal)
        {
            ["pricing_result_payload_invalid"] = new[] { 400, 413, 422 }.ToFrozenSet(),
            ["pricing_result_payload_conflict"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_result_job_agent_conflict"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_result_job_not_eligible"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_result_command_binding_invalid"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_cost_basis_approval_revoked"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_cloud_authority_revoked"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_cost_basis_approval_expired"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_cost_basis_approval_invalid"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_cost_basis_approval_required"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_job_authority_identity_invalid"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_job_authority_binding_missing"] = new[] { 409 }.ToFrozenSet(),
            ["pricing_job_authority_binding_invalid"] = new[] { 409 }.ToFrozenSet(),
            [NotCompleteCode] = new[] { 409 }.ToFrozenSet(),
        }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, FrozenSet<string>> TerminalErrors =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            ["pricing_result_payload_invalid"] = new[]
            {
                "Payload too large",
                "Invalid jobId",
                "Invalid JSON body",
                "Invalid pricing result payload (max 500 items)",
                "Pricing result payload is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_result_payload_conflict"] = new[]
            {
                "Pricing result conflicts with the accepted job",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_result_job_agent_conflict"] = new[]
            {
                "Pricing job is already bound to another workstation",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_result_job_not_eligible"] = new[]
            {
                "Pricing job requires manual reconciliation",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_result_command_binding_invalid"] = new[]
            {
                "Pricing command authorization is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_cost_basis_approval_revoked"] = new[]
            {
                "Pricing command authorization is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_cloud_authority_revoked"] = new[]
            {
                "Pricing command authorization is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_cost_basis_approval_expired"] = new[]
            {
                "Pricing command authorization is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_cost_basis_approval_invalid"] = new[]
            {
                "Pricing command authorization is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_cost_basis_approval_required"] = new[]
            {
                "Pricing command authorization is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_job_authority_identity_invalid"] = new[]
            {
                "Pricing command authorization is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_job_authority_binding_missing"] = new[]
            {
                "Pricing command authorization is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            ["pricing_job_authority_binding_invalid"] = new[]
            {
                "Pricing command authorization is invalid",
            }.ToFrozenSet(StringComparer.Ordinal),
            [NotCompleteCode] = new[]
            {
                "Only completed pricing results can be receipted",
            }.ToFrozenSet(StringComparer.Ordinal),
        }.ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly Regex EmailPattern = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PhonePattern = new(
        @"(?<!\d)(?:\+?1[-.\s]?)?(?:\(?\d{3}\)?[-.\s]?)\d{3}[-.\s]?\d{4}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LocalPathPattern = new(
        @"([A-Z]:\\|\\\\|/Users/|/home/|/var/folders/)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ControlTypePattern = new(
        @"^[A-Za-z][A-Za-z0-9.]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SafeEvidenceIdPattern = new(
        @"^[A-Za-z0-9:_-]{1,200}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly FrozenSet<string> BodyKeys = new[]
    {
        "commandId", "approvalId", "grantDigest", "status", "mode",
        "costBasis",
        "totalItems", "completedItems",
        "failedItems", "omittedInvalidItems", "omittedSelectorObservations", "items",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ItemKeys = new[]
    {
        "rowIndex", "ndc", "found", "supplierName", "costPerUnit",
        "packageCost",
        "baselineCostPerUnit", "quantity", "status", "confidence", "source",
        "localEvidenceId", "selectorObservations",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ObservationKeys = new[]
    {
        "stepId", "resolvedVia", "outcome", "failureKind", "attempted",
        "observedCandidates",
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ElementKeys =
        new[] { "controlType", "automationId", "className" }
            .ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> SafeSources =
        new[] { "sql", "uia", "vision" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> SelectorSteps =
        new[] { "OpenItemMenu", "OpenRxItem", "QuickSearchField", "VerifyNdc", "PricingTab", "SupplierGrid" }
            .ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> SelectorResolvedVia =
        new[] { "Builtin", "Learned" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> SelectorOutcomes =
        new[] { "Resolved", "FallbackUsed", "Failed" }
            .ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> SelectorFailureKinds =
        new[] { "None", "ElementNotFound", "VerifyMismatch", "FocusLost", "Timeout", "GridEmpty" }
            .ToFrozenSet(StringComparer.Ordinal);

    private sealed record CloudObservedElement(
        [property: JsonPropertyName("controlType")] string ControlType,
        [property: JsonPropertyName("automationId")] string? AutomationId,
        [property: JsonPropertyName("className")] string? ClassName);

    internal sealed record PersistedPricingPayload(string Json, int ItemCount);

    private sealed record CloudSafePricingResult(
        SupplierPriceResult Result,
        string CanonicalNdc);

    private sealed record SelectorProjection(
        ImmutableDictionary<int, object[]?> ByRowIndex,
        int IncludedCount,
        int OmittedCount);

    private sealed record SelectorProjectionState(
        int Remaining,
        ImmutableDictionary<int, object[]?> ByRowIndex,
        int IncludedCount);

    private readonly IPostSigner _postSigner;
    private readonly AgentStateDb _db;
    private readonly ILogger<PricingJobCloudUploader> _logger;
    private readonly string? _expectedAgentInstanceId;
    private readonly string? _expectedPharmacyId;
    private readonly IReadOnlyDictionary<string, string> _trustedApprovalKeys;
    private readonly TimeProvider _clock;

    public PricingJobCloudUploader(
        IPostSigner postSigner,
        AgentStateDb db,
        ILogger<PricingJobCloudUploader> logger,
        IReadOnlyDictionary<string, string>? trustedApprovalKeys = null,
        TimeProvider? clock = null)
    {
        _postSigner = postSigner;
        _db = db;
        _logger = logger;
        _expectedAgentInstanceId = NormalizeUuidV4(
            postSigner.BoundAgentInstanceId);
        _expectedPharmacyId = NormalizeUuidV4(postSigner.BoundPharmacyId);
        _trustedApprovalKeys = trustedApprovalKeys ??
            RemoteCommandTrust.CreateProductionKeyRegistry();
        _clock = clock ?? TimeProvider.System;
    }

    internal void PrepareDelivery(
        PricingJobSpec spec,
        string? commandId,
        Guid? sourceUploadId,
        PricingExecutorMode executorMode)
    {
        if (!SafeEvidenceIdPattern.IsMatch(spec.JobId) ||
            NormalizeUuidV4(commandId) is null)
            throw new InvalidOperationException("pricing_result_identity_invalid");
        _db.PreparePricingResultDelivery(
            spec,
            commandId,
            sourceUploadId,
            executorMode switch
            {
                PricingExecutorMode.SqlFirst => "sql",
                PricingExecutorMode.UiaFirst => "uia",
                PricingExecutorMode.VisionFirst => "vision",
                _ => throw new InvalidOperationException(
                    "pricing_result_executor_mode_invalid"),
            });
    }

    public async Task<PricingJobCloudUploadReceipt> UploadAsync(
        PricingJobSpec spec,
        PricingJobExecutionResult execution,
        string? commandId,
        CancellationToken ct) =>
        await UploadAsync(spec, execution, commandId, null, ct).ConfigureAwait(false);

    internal async Task<PricingJobCloudUploadReceipt> UploadAsync(
        PricingJobSpec spec,
        PricingJobExecutionResult execution,
        string? commandId,
        Guid? sourceUploadId,
        CancellationToken ct)
    {
        if (NormalizeUuidV4(commandId) is null)
            return new(false, CommandIneligibleCode, 0);

        if (!execution.Ok ||
            !string.Equals(
                execution.Progress.Status,
                PricingJobStatus.Completed,
                StringComparison.Ordinal))
        {
            // Failed/halted rows remain in pricing_results for a same-job
            // resume. They are working state, never immutable cloud evidence.
            return new(false, NotCompleteCode, 0);
        }

        AgentStateDb.PricingResultOutboxEntry? stagedEntry = null;
        try
        {
            // A normal command reaches this method after the terminal job
            // transaction has already serialized the exact immutable input
            // authority binding. Never rebuild those bytes from raw command
            // fields. The fallback exists only for pre-intent/manual callers.
            var entry = _db.GetPricingResultOutbox(spec.JobId);
            if (entry is null)
            {
                var results = _db.GetPricingResults(spec.JobId);
                var source = SourceFromExecutionMode(execution.Mode);
                var payload = BuildPersistedPayloadEnvelope(
                    spec.JobId,
                    commandId,
                    execution.Progress.Status,
                    source,
                    execution.Progress.TotalItems,
                    execution.Progress.CompletedItems,
                    execution.Progress.FailedItems,
                    results,
                    spec.ApprovalId,
                    spec.GrantDigest,
                    spec.CostBasis);
                entry = _db.StagePricingResultPayload(
                    spec.JobId, commandId, sourceUploadId, payload.Json,
                    payload.ItemCount,
                    execution.Ok);
            }
            stagedEntry = entry;
            var receipt = await SendPersistedAsync(entry, ct).ConfigureAwait(false);
            if (!receipt.Accepted && IsRetryable(receipt))
                _db.DelayPricingResultPayload(
                    entry.JobId, entry.PayloadSha256, entry.AttemptCount);
            return receipt;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            if (stagedEntry is not null && stagedEntry.State == "pending")
            {
                try
                {
                    _db.DelayPricingResultPayload(
                        stagedEntry.JobId,
                        stagedEntry.PayloadSha256,
                        stagedEntry.AttemptCount);
                }
                catch (InvalidOperationException)
                {
                    // Another retry path may have accepted it concurrently.
                }
            }
            return new(false, "pricing_result_upload_failed", 0);
        }
    }

    internal async Task FlushPendingAsync(
        CancellationToken ct,
        bool includeDeferred = false)
    {
        var entries = includeDeferred
            ? _db.GetAllPendingPricingResultPayloads(20)
            : _db.GetPendingPricingResultPayloads(20);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var receipt = await SendPersistedAsync(entry, ct).ConfigureAwait(false);
                if (!receipt.Accepted && IsRetryable(receipt))
                    _db.DelayPricingResultPayload(
                        entry.JobId, entry.PayloadSha256, entry.AttemptCount);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogSafeWarning(ex);
                _db.DelayPricingResultPayload(
                    entry.JobId, entry.PayloadSha256, entry.AttemptCount);
            }
        }
    }

    private async Task<PricingJobCloudUploadReceipt> SendPersistedAsync(
        AgentStateDb.PricingResultOutboxEntry entry,
        CancellationToken ct)
    {
        var priorTerminal = _db.GetPricingResultOutboxQuarantine(
            entry.JobId, entry.PayloadSha256);
        if (priorTerminal is not null)
            return new(
                false,
                priorTerminal.ReasonCode,
                0,
                priorTerminal.HttpStatus is not null ||
                IsPermanentPricingAuthorityFailure(
                    priorTerminal.ReasonCode));

        // Old versions could durably stage scheduled results with no signed
        // cloud command. Retain those bytes for audit, but terminalize them
        // locally and never put them on the wire.
        if (NormalizeUuidV4(entry.CommandId) is null)
            return Quarantine(entry, CommandIneligibleCode);

        if (!IsPersistedPayloadWithinCloudCeiling(entry.PayloadJson))
            return Quarantine(entry);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(entry.PayloadJson);
        }
        catch (JsonException)
        {
            return Quarantine(entry);
        }
        using (document)
        {
            if (!SafeEvidenceIdPattern.IsMatch(entry.JobId) ||
                !IsPersistedPayloadCloudSafe(
                    document.RootElement, entry.JobId, entry.ItemCount))
                return Quarantine(entry);
            if (!TryReadPersistedAuthorityBinding(
                    document.RootElement,
                    out var approvalId,
                    out var grantDigest))
                return Quarantine(entry);

            // Validate retained bytes before consulting runtime authority so
            // unsafe legacy evidence is quarantined even while disconnected.
            // The exact bound PIC authority is then rechecked immediately
            // before the only operation that can put those bytes on the wire.
            var send = await _db.ExecuteUnderPricingAuthorityAsync(
                entry.JobId,
                entry.PayloadSha256,
                approvalId,
                grantDigest,
                _clock,
                _trustedApprovalKeys,
                (context, token) => SendOrRecoverPricingResultAsync(
                    entry,
                    document.RootElement.Clone(),
                    approvalId,
                    grantDigest,
                    context,
                    token),
                ct).ConfigureAwait(false);
            if (!send.Admitted)
            {
                var authorityCode = send.Code;
                if (IsPermanentPricingAuthorityFailure(authorityCode))
                    return Quarantine(
                        entry,
                        authorityCode,
                        verifiedTerminal: true);
                _logger.LogWarning(
                    "pricing_result_authority_deferred code={Code}",
                    send.Code);
                return new(false, send.Code, 0);
            }

            return send.Value ??
                new(false, "pricing_result_upload_failed", 0);
        }
    }

    private async Task<PricingJobCloudUploadReceipt>
        SendAndCommitPricingResultAsync(
            AgentStateDb.PricingResultOutboxEntry entry,
            JsonElement payload,
            CancellationToken ct)
    {
        var verified = await _postSigner.PostSignedResponseVerifiedAsync(
            $"/api/agent/pricing-jobs/{entry.JobId}/results",
            payload,
            ct).ConfigureAwait(false);
        if (verified is null || !HasValidVerifiedEnvelope(verified))
            return new(false, "pricing_result_response_signature_invalid", 0);
        if (verified.StatusCode is < 200 or > 299)
        {
            if (!TryParseTerminalRejection(
                    verified.StatusCode,
                    verified.Body,
                    out var code,
                    out var exactResponse))
                return new(false, "pricing_result_upload_failed", 0);
            return Quarantine(
                entry,
                code,
                verified.StatusCode,
                exactResponse,
                verified.KeyId,
                verified.SignatureBase64,
                verifiedTerminal: true);
        }

        if (!TryParseSuccessReceipt(
                verified.Body,
                entry,
                _expectedAgentInstanceId,
                _expectedPharmacyId,
                requireIdempotent: false,
                out var recordedCount))
        {
            _logger.LogWarning("pricing_result_upload_receipt_invalid");
            return new(false, "pricing_result_upload_receipt_invalid", 0);
        }

        // The authority gate remains held through this durable receipt commit.
        // A response-loss crash retains the pre-wire exact-attempt marker and
        // may reconcile only those same immutable payload bytes after revoke.
        _db.MarkPricingResultPayloadAccepted(
            entry.JobId,
            entry.PayloadSha256,
            recordedCount,
            "pricing_result_upload_accepted",
            verified.Body,
            verified.KeyId,
            verified.SignatureBase64);
        return new(true, "pricing_result_upload_accepted", recordedCount);
    }

    private PricingJobCloudUploadReceipt Quarantine(
        AgentStateDb.PricingResultOutboxEntry entry,
        string reasonCode = ContentBlockedCode,
        int? httpStatus = null,
        string? responseJson = null,
        string? responseKeyId = null,
        string? responseSignature = null,
        bool verifiedTerminal = false)
    {
        // Legacy immutable evidence is retained byte-for-byte. A separate,
        // append-only terminal record removes it from every automatic retry set.
        _db.QuarantinePricingResultPayload(
            entry.JobId,
            entry.PayloadSha256,
            reasonCode,
            httpStatus,
            responseJson,
            responseKeyId,
            responseSignature);
        _logger.LogError("{Code}", reasonCode);
        return new(false, reasonCode, 0, verifiedTerminal);
    }

    private static bool IsRetryable(PricingJobCloudUploadReceipt receipt) =>
        !string.Equals(receipt.Code, ContentBlockedCode, StringComparison.Ordinal) &&
        !string.Equals(receipt.Code, CommandIneligibleCode, StringComparison.Ordinal) &&
        !IsPermanentPricingAuthorityFailure(receipt.Code) &&
        !TerminalStatuses.ContainsKey(receipt.Code);

    private static bool IsPermanentPricingAuthorityFailure(string code) => code is
        "pricing_cost_basis_approval_revoked" or
        "pricing_cloud_authority_revoked" or
        ManualReconciliationCode or
        "pricing_cost_basis_approval_expired" or
        "pricing_cost_basis_approval_invalid" or
        "pricing_cost_basis_approval_required" or
        "pricing_job_authority_identity_invalid" or
        "pricing_job_authority_binding_missing" or
        "pricing_job_authority_binding_invalid";

    internal static bool IsPersistedPayloadWithinCloudCeiling(string payloadJson) =>
        !string.IsNullOrEmpty(payloadJson) &&
        PricingResultPayloadBudget.SerializedSize(payloadJson) <=
            PricingResultPayloadBudget.MaximumSerializedBytes;

    internal static string BuildPersistedPayload(
        string jobId,
        string? commandId,
        string status,
        string source,
        int totalItems,
        int completedItems,
        int failedItems,
        IReadOnlyList<SupplierPriceResult> results,
        string? approvalId = null,
        string? grantDigest = null,
        string costBasis = PricingApprovalContract.CostPerUnitBasis) =>
        BuildPersistedPayloadEnvelope(
            jobId, commandId, status, source, totalItems, completedItems,
            failedItems, results, approvalId, grantDigest, costBasis).Json;

    internal static PersistedPricingPayload BuildPersistedPayloadEnvelope(
        string jobId,
        string? commandId,
        string status,
        string source,
        int totalItems,
        int completedItems,
        int failedItems,
        IReadOnlyList<SupplierPriceResult> results,
        string? approvalId = null,
        string? grantDigest = null,
        string costBasis = PricingApprovalContract.CostPerUnitBasis)
    {
        if (!SafeEvidenceIdPattern.IsMatch(jobId) ||
            commandId is not null && !SafeEvidenceIdPattern.IsMatch(commandId) ||
            (approvalId is null) != (grantDigest is null) ||
            !PricingApprovalContract.IsSupportedCostBasis(costBasis))
            throw new InvalidOperationException("pricing_result_identity_invalid");
        if (!string.Equals(status, PricingJobStatus.Completed, StringComparison.Ordinal))
            throw new InvalidOperationException("pricing_result_not_complete");

        var safeResults = results
            .Select(result => new CloudSafePricingResult(
                PricingResultContentPolicy.NormalizeForPersistence(result),
                PricingResultContentPolicy.CanonicalNdcOrNull(result.Ndc) ?? ""))
            .Where(result => result.CanonicalNdc.Length == 11)
            .ToArray();
        if (safeResults.Any(result => result.Result.CostBasis != costBasis))
            throw new InvalidOperationException("pricing_result_cost_basis_mismatch");
        var omittedInvalidItems = results.Count - safeResults.Length;
        var totalSelectorObservations = CountSelectorObservations(results);
        var derivedCompletedItems = safeResults.Count(result => result.Result.Found);
        var derivedFailedItems =
            safeResults.Length - derivedCompletedItems + omittedInvalidItems;
        if (!PricingResultPayloadBudget.AreSerializedMetricsValid(
                totalItems,
                completedItems,
                failedItems,
                safeResults.Length,
                omittedInvalidItems) ||
            totalItems != results.Count ||
            completedItems != derivedCompletedItems ||
            failedItems != derivedFailedItems ||
            safeResults.Any(result => result.Result.RowIndex is < 0 or > 1_000_000) ||
            safeResults.Select(result => result.Result.RowIndex).Distinct().Count() !=
                safeResults.Length)
            throw new InvalidOperationException("pricing_result_metrics_out_of_range");

        var withObservations = SerializePersistedPayload(
            jobId, commandId, approvalId, grantDigest, status, source,
            costBasis,
            totalItems, completedItems,
            failedItems, omittedInvalidItems, totalSelectorObservations,
            safeResults, includeObservations: true);
        if (PricingResultPayloadBudget.SerializedSize(withObservations) <=
            PricingResultPayloadBudget.MaximumSerializedBytes)
            return new PersistedPricingPayload(withObservations, safeResults.Length);

        // Selector evidence is explicitly optional. Drop it as one deterministic
        // unit before considering the required pricing result body oversized.
        var requiredOnly = SerializePersistedPayload(
            jobId, commandId, approvalId, grantDigest, status, source,
            costBasis,
            totalItems, completedItems,
            failedItems, omittedInvalidItems, totalSelectorObservations,
            safeResults, includeObservations: false);
        if (PricingResultPayloadBudget.SerializedSize(requiredOnly) >
            PricingResultPayloadBudget.MaximumSerializedBytes)
            throw new InvalidOperationException("pricing_result_payload_too_large");
        return new PersistedPricingPayload(requiredOnly, safeResults.Length);
    }

    private static string SerializePersistedPayload(
        string jobId,
        string? commandId,
        string? approvalId,
        string? grantDigest,
        string status,
        string source,
        string costBasis,
        int totalItems,
        int completedItems,
        int failedItems,
        int omittedInvalidItems,
        int totalSelectorObservations,
        IReadOnlyList<CloudSafePricingResult> safeResults,
        bool includeObservations)
    {
        var selectorProjection = ProjectSelectorObservations(
            safeResults, totalSelectorObservations, includeObservations);
        var payload = new
        {
            commandId,
            approvalId,
            grantDigest,
            status,
            mode = SafeSource(source),
            costBasis,
            totalItems,
            completedItems,
            failedItems,
            omittedInvalidItems,
            omittedSelectorObservations = selectorProjection.OmittedCount,
            items = safeResults.Select(item => new
            {
                rowIndex = item.Result.RowIndex,
                ndc = item.CanonicalNdc,
                found = item.Result.Found,
                supplierName = PricingResultContentPolicy.CloudSafeSupplierName(
                    item.Result.SupplierName),
                costPerUnit = item.Result.CostPerUnit,
                packageCost = item.Result.PackageCost,
                // Preserve the existing PHI-negative payload contract exactly.
                baselineCostPerUnit = item.Result.BaselineCostPerUnit,
                quantity = item.Result.Quantity,
                status = item.Result.Found ? "found" : "not_found",
                confidence = item.Result.Found ? 0.95m : 0.35m,
                source = SafeSource(source),
                localEvidenceId = $"pricing:{jobId}:{item.Result.RowIndex}",
                selectorObservations = selectorProjection.ByRowIndex.TryGetValue(
                    item.Result.RowIndex, out var observations)
                        ? observations
                        : null,
            }).ToArray(),
        };
        return JsonSerializer.Serialize(payload);
    }

    // M2a: selector telemetry contains only structural UIA properties, but those
    // properties are vendor-provided strings. Enforce the cloud grammar here;
    // never treat the property name or record type as proof that a dynamic value
    // did not slip into AutomationId/ClassName.
    private static int CountSelectorObservations(
        IReadOnlyList<SupplierPriceResult> results)
    {
        var total = results.Aggregate(
            0L,
            (count, result) => Math.Min(
                PricingSelectorObservationPolicy.MaximumTotalObservations + 1L,
                count + result.OmittedSelectorObservations +
                    (result.Observations?.Count ?? 0)));
        if (total > PricingSelectorObservationPolicy.MaximumTotalObservations)
            throw new InvalidOperationException(
                "pricing_result_selector_observations_out_of_range");
        return (int)total;
    }

    private static SelectorProjection ProjectSelectorObservations(
        IReadOnlyList<CloudSafePricingResult> safeResults,
        int totalCount,
        bool includeObservations)
    {
        var initial = new SelectorProjectionState(
            includeObservations
                ? PricingSelectorObservationPolicy.MaximumIncludedCloudObservations
                : 0,
            ImmutableDictionary<int, object[]?>.Empty,
            0);
        var projected = safeResults.Aggregate(initial, (state, item) =>
        {
            var mapped = MapObservations(
                item.Result.Observations,
                state.Remaining);
            return new SelectorProjectionState(
                state.Remaining - mapped.Length,
                state.ByRowIndex.Add(
                    item.Result.RowIndex,
                    mapped.Length == 0 ? null : mapped),
                state.IncludedCount + mapped.Length);
        });
        if (projected.IncludedCount > totalCount)
            throw new InvalidOperationException(
                "pricing_result_selector_observations_out_of_range");
        return new SelectorProjection(
            projected.ByRowIndex,
            projected.IncludedCount,
            totalCount - projected.IncludedCount);
    }

    private static object[] MapObservations(
        IReadOnlyList<SuavoAgent.Contracts.Learning.SelectorObservation>? observations,
        int maximumCount)
    {
        if (observations is not { Count: > 0 } || maximumCount <= 0)
            return [];
        return observations
            .Select(MapObservation)
            .Where(observation => observation is not null)
            .Cast<object>()
            .Take(maximumCount)
            .ToArray();
    }

    private static object? MapObservation(
        SuavoAgent.Contracts.Learning.SelectorObservation observation)
    {
        if (!Enum.IsDefined(observation.StepId) ||
            !Enum.IsDefined(observation.ResolvedVia) ||
            !Enum.IsDefined(observation.Outcome) ||
            !Enum.IsDefined(observation.FailureKind) ||
            observation.ObservedCandidates is null ||
            observation.ObservedCandidates.Count >
                PricingSelectorObservationPolicy.MaximumCandidatesPerObservation)
            return null;
        var attempted = MapElement(observation.Attempted);
        if (observation.Attempted is not null && attempted is null)
            return null;
        var mappedCandidates = observation.ObservedCandidates
            .Select(MapElement)
            .ToArray();
        if (mappedCandidates.Any(candidate => candidate is null))
            return null;
        var candidates = mappedCandidates
            .Cast<CloudObservedElement>()
            .ToArray();
        return new
        {
            stepId = observation.StepId.ToString(),
            resolvedVia = observation.ResolvedVia.ToString(),
            outcome = observation.Outcome.ToString(),
            failureKind = observation.FailureKind.ToString(),
            attempted,
            observedCandidates = candidates,
        };
    }

    private static CloudObservedElement? MapElement(
        SuavoAgent.Contracts.Learning.ObservedElement? element)
    {
        if (element is null || string.IsNullOrEmpty(element.ControlType) ||
            !ControlTypePattern.IsMatch(element.ControlType))
            return null;
        if (element.AutomationId is not null &&
            !IsCloudSafeStructuralIdentifier(element.AutomationId))
            return null;
        if (element.ClassName is not null &&
            !IsCloudSafeStructuralIdentifier(element.ClassName))
            return null;
        return new(element.ControlType, element.AutomationId, element.ClassName);
    }

    private static bool IsCloudSafeStructuralIdentifier(string value) =>
        StructuralIdentifierSanitizer.IsAllowed(value) &&
        !EmailPattern.IsMatch(value) &&
        !PhonePattern.IsMatch(value) &&
        !LocalPathPattern.IsMatch(value);

}
