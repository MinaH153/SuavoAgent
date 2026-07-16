using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

internal static class FetchPatientCommandContract
{
    private static readonly HashSet<string> ExactFields = new(StringComparer.Ordinal)
    {
        "candidateId",
        "rxHash",
        "evidenceId",
        "pharmacyId",
        "commandId",
        "sourceKind",
        "sourceBinding",
    };

    internal static bool TryParse(
        JsonElement data,
        out ApprovedPatientFetchCommand? command,
        out string rejectionCode)
    {
        command = null;
        rejectionCode = "fetch_data_invalid";
        if (data.ValueKind != JsonValueKind.Object) return false;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in data.EnumerateObject())
        {
            var validValue = property.Name == "sourceBinding"
                ? property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null
                : property.Value.ValueKind == JsonValueKind.String;
            if (!ExactFields.Contains(property.Name) || !seen.Add(property.Name) || !validValue)
            {
                rejectionCode = "fetch_data_shape_mismatch";
                return false;
            }
        }
        if (!seen.SetEquals(ExactFields))
        {
            rejectionCode = "fetch_data_shape_mismatch";
            return false;
        }

        var candidateId = data.GetProperty("candidateId").GetString()!;
        var rxHash = data.GetProperty("rxHash").GetString()!;
        var evidenceId = data.GetProperty("evidenceId").GetString()!;
        var pharmacyId = data.GetProperty("pharmacyId").GetString()!;
        var commandId = data.GetProperty("commandId").GetString()!;
        var sourceKind = data.GetProperty("sourceKind").GetString()!;
        var sourceBinding = data.GetProperty("sourceBinding").GetString();

        if (!IsCanonicalUuid(candidateId) || !IsCanonicalUuid(pharmacyId) || !IsCanonicalUuid(commandId))
        {
            rejectionCode = "fetch_identifier_invalid";
            return false;
        }
        if (sourceKind == RxCorrelationSourceKinds.PioneerRxBuiltIn)
        {
            if (sourceBinding is not null)
            {
                rejectionCode = "fetch_source_invalid";
                return false;
            }
        }
        else if (sourceKind != RxCorrelationSourceKinds.LearnedApproved ||
                 sourceBinding is not { Length: 64 } ||
                 sourceBinding.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            rejectionCode = "fetch_source_invalid";
            return false;
        }
        if (rxHash.Length != 64 || rxHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            rejectionCode = "fetch_hash_invalid";
            return false;
        }

        var evidencePrefix = "rxh-" + rxHash[..16] + "-";
        if (!evidenceId.StartsWith(evidencePrefix, StringComparison.Ordinal) ||
            evidenceId.Length < evidencePrefix.Length + 10 ||
            evidenceId.Length > evidencePrefix.Length + 13 ||
            evidenceId[evidencePrefix.Length..].Any(c => c is < '0' or > '9'))
        {
            rejectionCode = "fetch_evidence_invalid";
            return false;
        }

        command = new ApprovedPatientFetchCommand(
            candidateId,
            rxHash,
            evidenceId,
            pharmacyId,
            commandId,
            sourceKind,
            sourceBinding);
        rejectionCode = "accepted";
        return true;
    }

    private static bool IsCanonicalUuid(string value) =>
        value.Length == 36 &&
        Guid.TryParseExact(value, "D", out var parsed) &&
        string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal);
}

internal sealed record PatientDetailsCallbackReceipt(
    string CommandId,
    string CandidateId,
    string PharmacyId,
    string StagingId,
    string TransitionId,
    string Status,
    string ReviewState,
    DateTimeOffset ExpiresAtUtc,
    bool Idempotent);

internal sealed record PatientLookupResult(
    bool SourceAvailable,
    RxPatientDetails? Details,
    string? FailureCategory = null)
{
    internal static PatientLookupResult Unavailable(string category) => new(false, null, category);
    internal static PatientLookupResult Found(RxPatientDetails? details) => new(true, details);
}

internal interface IApprovedPatientSource
{
    Task<PatientLookupResult> ReadAsync(
        PendingApprovedPatientFetch pending,
        string rawRxNumber,
        CancellationToken ct);
}

internal interface IApprovedPatientCloudTransport
{
    Task<PatientDetailsCallbackReceipt?> SendCallbackAsync(
        ApprovedPatientFetchCommand command,
        PatientDetailsPayload details,
        CancellationToken ct);

    Task<bool> AckAsync(string commandId, object result, CancellationToken ct);

    Task<bool> AckFailureAsync(
        string commandId,
        object result,
        string error,
        CancellationToken ct);
}

internal sealed class PioneerRxApprovedPatientSource : IApprovedPatientSource
{
    private readonly IServiceProvider _services;

    internal PioneerRxApprovedPatientSource(IServiceProvider services) => _services = services;

    public async Task<PatientLookupResult> ReadAsync(
        PendingApprovedPatientFetch pending,
        string rawRxNumber,
        CancellationToken ct)
    {
        if (pending.SourceKind == RxCorrelationSourceKinds.PioneerRxBuiltIn &&
            pending.SourceBinding is null)
        {
            var worker = _services.GetService<RxDetectionWorker>();
            if (worker?.SqlEngine is null || !worker.IsSqlConnected)
                return PatientLookupResult.Unavailable("builtin_source_unavailable");
            var details = await worker.SqlEngine.PullPatientForRxAsync(rawRxNumber, ct).ConfigureAwait(false);
            return PatientLookupResult.Found(details);
        }

        if (pending.SourceKind != RxCorrelationSourceKinds.LearnedApproved ||
            pending.SourceBinding is null)
            return PatientLookupResult.Unavailable("source_binding_invalid");

        var registry = _services.GetService<IActivePmsAdapterRegistry>();
        using var lease = registry?.TryAcquire(DateTimeOffset.UtcNow);
        if (lease is null ||
            !string.Equals(
                lease.Binding.PharmacyId,
                pending.Key.PharmacyId,
                StringComparison.Ordinal) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(lease.Binding.TemplateDigest),
                Encoding.ASCII.GetBytes(pending.SourceBinding)))
            return PatientLookupResult.Unavailable("source_binding_inactive");
        if (lease.Adapter is not LearnedPmsAdapter learned || !learned.SupportsPatientLookup)
        {
            registry!.ReportUnhealthy(
                lease.Binding,
                DateTimeOffset.UtcNow,
                "approved_patient_lookup_unavailable");
            return PatientLookupResult.Unavailable("patient_contract_unavailable");
        }

        try
        {
            var details = await learned.PullPatientForRxAsync(rawRxNumber, ct).ConfigureAwait(false);
            registry!.ReportHealthy(lease.Binding, DateTimeOffset.UtcNow);
            return PatientLookupResult.Found(details);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            registry!.ReportUnhealthy(
                lease.Binding,
                DateTimeOffset.UtcNow,
                ex.GetType().Name);
            throw;
        }
    }
}

internal sealed class SuavoApprovedPatientCloudTransport : IApprovedPatientCloudTransport
{
    private readonly SuavoCloudClient _client;

    internal SuavoApprovedPatientCloudTransport(SuavoCloudClient client) => _client = client;

    public Task<PatientDetailsCallbackReceipt?> SendCallbackAsync(
        ApprovedPatientFetchCommand command,
        PatientDetailsPayload details,
        CancellationToken ct) =>
        _client.SendApprovedPatientDetailsAsync(command, details, ct);

    public Task<bool> AckAsync(string commandId, object result, CancellationToken ct) =>
        _client.TryAckCommandAsync(commandId, true, result, null, ct);

    public Task<bool> AckFailureAsync(
        string commandId,
        object result,
        string error,
        CancellationToken ct) =>
        _client.TryAckCommandAsync(commandId, false, result, error, ct);
}

/// <summary>
/// Durable approved-only Rx retrieval. Registration is PHI-free. Retries decrypt the raw Rx
/// lookup key only after a valid signed command has bound the exact local evidence record, verify
/// the HMAC in fixed time, query PioneerRx, require a command-key-signed callback receipt, and only
/// then acknowledge the cloud command.
/// </summary>
internal sealed class ApprovedPatientRetrievalCoordinator
{
    private const int MaxRetriesPerHeartbeat = 8;
    private readonly string _pharmacyId;
    private readonly string _agentId;
    private readonly string _machineFingerprint;
    private readonly string _hmacSalt;
    private readonly bool _egressEnabled;
    private readonly IRxCorrelationStore _store;
    private readonly AgentStateDb _stateDb;
    private readonly IApprovedPatientSource _source;
    private readonly IApprovedPatientCloudTransport _cloud;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    internal ApprovedPatientRetrievalCoordinator(
        AgentOptions options,
        IRxCorrelationStore store,
        AgentStateDb stateDb,
        IApprovedPatientSource source,
        IApprovedPatientCloudTransport cloud,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _pharmacyId = options.PharmacyId ?? "";
        _agentId = options.AgentId ?? "";
        _machineFingerprint = options.MachineFingerprint ?? "";
        _hmacSalt = options.HmacSalt ?? "";
        _egressEnabled = options.EnableAuditedPatientDetailsEgress;
        _store = store;
        _stateDb = stateDb;
        _source = source;
        _cloud = cloud;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal RxCorrelationRegistrationResult Register(ApprovedPatientFetchCommand command)
    {
        if (!IdentityConfigured() ||
            !string.Equals(command.PharmacyId, _pharmacyId, StringComparison.Ordinal))
        {
            return new RxCorrelationRegistrationResult(RxCorrelationRegistrationCode.IdentityMismatch, null);
        }

        var result = _store.RegisterApprovedFetch(command, _agentId, _machineFingerprint);
        if (result.Accepted)
        {
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: command.RxHash,
                EventType: "approved_patient_fetch_registered",
                FromState: "observed_hash_only",
                ToState: "patient_fetch_pending",
                Trigger: "fetch_patient",
                CommandId: command.CommandId,
                RequesterId: command.CandidateId,
                Actor: "pharmacist",
                SourceComponent: "approved_patient_retrieval",
                CaptureReason: result.Code == RxCorrelationRegistrationCode.Idempotent
                    ? "idempotent_signed_approval"
                    : "signed_pharmacist_approval"));
        }
        return result;
    }

    internal async Task RetryPendingAsync(CancellationToken ct)
    {
        try
        {
            _store.PruneExpired();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
        {
            _logger.LogError(
                "Approved patient retrieval store maintenance failed ({ErrorType}); failing closed",
                ex.GetType().Name);
            return;
        }

        if (IdentityConfigured())
            await AckTerminalFailuresAsync(ct).ConfigureAwait(false);

        // If the audited route/migration rollout gate is closed, do not even read patient PHI.
        // The durable hash-only approval remains queued for a coordinated restart after enablement.
        if (!IdentityConfigured() || !_egressEnabled) return;

        IReadOnlyList<PendingApprovedPatientFetch> pending;
        try
        {
            pending = _store.GetPending(
                _pharmacyId,
                _agentId,
                _machineFingerprint,
                MaxRetriesPerHeartbeat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
        {
            _logger.LogError(
                "Approved patient retrieval store unavailable ({ErrorType}); failing closed",
                ex.GetType().Name);
            return;
        }

        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await ProcessOneAsync(item, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Never log exception messages or patient fields: SQL/network exceptions can echo
                // query values. Type + opaque command id are enough for operational diagnosis.
                _logger.LogWarning(
                    "Approved patient retrieval deferred for command {CommandId} ({ErrorType})",
                    item.CommandId,
                    ex.GetType().Name);
                _store.DeferPatientFetch(
                    item,
                    "source_or_transport_error",
                    quarantine: false);
            }
        }

        // A deterministic failure or the final bounded retry can quarantine an
        // item during this pass. Converge the cloud outbox in the same heartbeat.
        await AckTerminalFailuresAsync(ct).ConfigureAwait(false);
    }

    private async Task AckTerminalFailuresAsync(CancellationToken ct)
    {
        IReadOnlyList<PendingApprovedPatientFailure> failures;
        try
        {
            failures = _store.GetUnacknowledgedFailures(
                _pharmacyId,
                _agentId,
                _machineFingerprint,
                MaxRetriesPerHeartbeat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
        {
            _logger.LogError(
                "Approved patient terminal failures unavailable ({ErrorType}); failing closed",
                ex.GetType().Name);
            return;
        }

        foreach (var failure in failures)
        {
            ct.ThrowIfCancellationRequested();
            var acknowledged = await _cloud.AckFailureAsync(
                failure.CommandId,
                new
                {
                    status = "patient_details_failed",
                    candidateId = failure.CandidateId,
                    failureCode = failure.FailureCategory,
                },
                failure.FailureCategory,
                ct).ConfigureAwait(false);
            if (!acknowledged) continue;
            _store.MarkFailureAcknowledged(
                failure,
                _pharmacyId,
                _agentId,
                _machineFingerprint);
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: failure.CommandId,
                EventType: "approved_patient_fetch_failed",
                FromState: "patient_fetch_pending",
                ToState: "command_failed",
                Trigger: "fetch_patient",
                CommandId: failure.CommandId,
                RequesterId: failure.CandidateId,
                Actor: "agent",
                SourceComponent: "approved_patient_retrieval",
                CaptureReason: failure.FailureCategory));
        }
    }

    private async Task ProcessOneAsync(PendingApprovedPatientFetch pending, CancellationToken ct)
    {
        if (pending.State == RxCorrelationCommandState.AwaitingCallback)
        {
            if (!_store.TryRevealRawRx(pending, out var rawRxNumber)) return;
            try
            {
                if (!FixedTimeRxHashMatches(rawRxNumber, _hmacSalt, pending.Key.RxHash))
                {
                    _logger.LogError(
                        "Approved patient retrieval hash mismatch for command {CommandId}; failing closed",
                        pending.CommandId);
                    _store.DeferPatientFetch(
                        pending,
                        "correlation_hash_mismatch",
                        quarantine: true);
                    return;
                }

                // HIPAA audit occurs immediately before the first patient-bearing SQL read.
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    TaskId: pending.Key.RxHash,
                    EventType: "phi_access",
                    FromState: "patient_fetch_pending",
                    ToState: "patient_query_started",
                    Trigger: "fetch_patient",
                    CommandId: pending.CommandId,
                    RequesterId: pending.CandidateId,
                    Actor: "pharmacist",
                    SourceComponent: "approved_patient_retrieval",
                    CaptureReason: "minimum_necessary_delivery_fields"));

                var lookup = await _source.ReadAsync(pending, rawRxNumber, ct).ConfigureAwait(false);
                if (!lookup.SourceAvailable)
                {
                    _store.DeferPatientFetch(
                        pending,
                        lookup.FailureCategory ?? "source_unavailable",
                        quarantine: lookup.FailureCategory is
                            "source_binding_invalid" or "patient_contract_unavailable");
                    return;
                }
                if (lookup.Details is null)
                {
                    _store.DeferPatientFetch(pending, "patient_not_found", quarantine: false);
                    return;
                }
                if (!FixedTimeTextEquals(rawRxNumber, lookup.Details.RxNumber))
                {
                    _logger.LogError(
                        "Approved patient retrieval returned a mismatched lookup row for command {CommandId}; failing closed",
                        pending.CommandId);
                    _store.DeferPatientFetch(
                        pending,
                        "lookup_identity_mismatch",
                        quarantine: true);
                    return;
                }

                var command = new ApprovedPatientFetchCommand(
                    pending.CandidateId,
                    pending.Key.RxHash,
                    pending.Key.EvidenceId,
                    pending.Key.PharmacyId,
                    pending.CommandId,
                    pending.SourceKind,
                    pending.SourceBinding);
                var details = PatientDetailsPayload.FromRxPatientDetails(lookup.Details);
                var receipt = await _cloud.SendCallbackAsync(command, details, ct).ConfigureAwait(false);
                if (receipt is null)
                {
                    _store.DeferPatientFetch(pending, "callback_unavailable", quarantine: false);
                    return;
                }

                _store.MarkCallbackAccepted(
                    pending,
                    receipt.StagingId,
                    receipt.TransitionId,
                    receipt.ExpiresAtUtc);
                pending = pending with
                {
                    State = RxCorrelationCommandState.CallbackAccepted,
                    CallbackExpiresAtUtc = receipt.ExpiresAtUtc,
                };
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    TaskId: pending.Key.RxHash,
                    EventType: "patient_details_callback_accepted",
                    FromState: "patient_query_started",
                    ToState: "patient_details_received",
                    Trigger: "fetch_patient",
                    CommandId: pending.CommandId,
                    RequesterId: pending.CandidateId,
                    Actor: "agent",
                    SourceComponent: "approved_patient_retrieval",
                    CaptureReason: receipt.Idempotent
                        ? "authenticated_idempotent_receipt"
                        : "authenticated_created_receipt"));
            }
            finally
            {
                ClearTransientString(rawRxNumber);
            }
        }

        if (pending.State != RxCorrelationCommandState.CallbackAccepted) return;
        if (pending.CallbackExpiresAtUtc is null ||
            pending.CallbackExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            _store.DeferPatientFetch(
                pending,
                "callback_receipt_expired",
                quarantine: true);
            return;
        }
        var acknowledged = await _cloud.AckAsync(
            pending.CommandId,
            new
            {
                status = "patient_details_received",
                candidateId = pending.CandidateId,
            },
            ct).ConfigureAwait(false);
        if (!acknowledged)
        {
            _store.DeferPatientFetch(pending, "command_ack_unavailable", quarantine: false);
            return;
        }

        _store.MarkCompleted(pending);
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: pending.Key.RxHash,
            EventType: "approved_patient_fetch_completed",
            FromState: "patient_details_received",
            ToState: "command_acked",
            Trigger: "fetch_patient",
            CommandId: pending.CommandId,
            RequesterId: pending.CandidateId,
            Actor: "agent",
            SourceComponent: "approved_patient_retrieval",
            CaptureReason: "callback_receipt_verified_before_ack"));
    }

    internal static bool FixedTimeRxHashMatches(string rawRxNumber, string hmacSalt, string expectedHash)
    {
        if (string.IsNullOrEmpty(hmacSalt) || expectedHash.Length != 64) return false;

        byte[] expected;
        try { expected = Convert.FromHexString(expectedHash); }
        catch (FormatException) { return false; }

        var key = Encoding.UTF8.GetBytes(hmacSalt);
        var raw = Encoding.UTF8.GetBytes(rawRxNumber);
        try
        {
            var actual = HMACSHA256.HashData(key, raw);
            try { return CryptographicOperations.FixedTimeEquals(actual, expected); }
            finally { CryptographicOperations.ZeroMemory(actual); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private bool IdentityConfigured() =>
        !string.IsNullOrWhiteSpace(_pharmacyId) &&
        !string.IsNullOrWhiteSpace(_agentId) &&
        !string.IsNullOrWhiteSpace(_machineFingerprint) &&
        !string.IsNullOrWhiteSpace(_hmacSalt);

    private static void ClearTransientString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static bool FixedTimeTextEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        try { return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b); }
        finally
        {
            CryptographicOperations.ZeroMemory(a);
            CryptographicOperations.ZeroMemory(b);
        }
    }
}
