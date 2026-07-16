using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Vision;

internal enum VisionConfigurationOutboxState
{
    PendingApply,
    PendingAck,
    Acked,
}

internal sealed record VisionConfigurationOutboxRegistration(
    string CommandId,
    string ConfigDigest,
    string OptionsDocument,
    string? BundleUrl,
    string? BundleSha256,
    string EnvelopeNonce,
    string EnvelopeBinding,
    DateTimeOffset RegisteredAt);

internal sealed record VisionConfigurationOutboxItem(
    string CommandId,
    string ConfigDigest,
    string OptionsDocument,
    string? BundleUrl,
    string? BundleSha256,
    string EnvelopeNonce,
    string EnvelopeBinding,
    VisionConfigurationOutboxState State,
    bool ApplySucceeded,
    long? Generation,
    string? ResultCode,
    DateTimeOffset RegisteredAt,
    DateTimeOffset UpdatedAt);

internal sealed record VisionConfigurationOutboxRegisterResult(
    bool Accepted,
    bool Idempotent,
    string Code,
    VisionConfigurationOutboxItem? Item = null);

internal interface IVisionConfigurationCommandLedger
{
    VisionConfigurationOutboxRegisterResult RegisterVisionConfiguration(
        VisionConfigurationOutboxRegistration registration);
    IReadOnlyList<VisionConfigurationOutboxItem> GetPendingVisionConfigurations(int maximum);
    bool MarkVisionConfigurationPendingAck(
        string commandId,
        string configDigest,
        long? generation,
        bool applySucceeded,
        string resultCode);
    bool MarkVisionConfigurationAcked(string commandId, string configDigest);
    void RecordVisionConfigurationStructuralFailure(
        string envelopeBinding,
        string? commandId,
        string code);
    (string Code, DateTimeOffset RecordedAt)? GetLatestVisionConfigurationStructuralFailure();
}

/// <summary>
/// Durable local execution/ACK outbox for <c>set_vision_config</c>. Registration
/// happens before envelope nonce persistence. Every later boundary is
/// idempotent, so a crash after registry apply or cloud ACK converges safely.
/// </summary>
internal sealed class VisionConfigurationCommandOutbox
{
    private readonly IVisionConfigurationCommandLedger _ledger;
    private readonly VisionConfigurationCoordinator _coordinator;
    private readonly VisionConfigurationStatusProvider _status;
    private readonly string _dataDirectory;
    private readonly Func<VisionOptionsSnapshot, bool> _verifyPreinstalledCohort;
    private readonly Func<string, bool, object?, string?, CancellationToken, Task<bool>> _ack;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _retryGate = new(1, 1);

    internal VisionConfigurationCommandOutbox(
        IVisionConfigurationCommandLedger ledger,
        VisionConfigurationCoordinator coordinator,
        VisionConfigurationStatusProvider status,
        string dataDirectory,
        Func<VisionOptionsSnapshot, bool> verifyPreinstalledCohort,
        Func<string, bool, object?, string?, CancellationToken, Task<bool>> ack,
        ILogger logger,
        Func<DateTimeOffset>? utcNow = null)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _status = status ?? throw new ArgumentNullException(nameof(status));
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _verifyPreinstalledCohort = verifyPreinstalledCohort ??
                                    throw new ArgumentNullException(nameof(verifyPreinstalledCohort));
        _ack = ack ?? throw new ArgumentNullException(nameof(ack));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal VisionConfigurationOutboxRegisterResult RegisterVerified(
        VisionConfigurationCommand command,
        SignedCommand envelope)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(envelope);
        var prototype = VisionConfigurationStateCodec.Create(
            1,
            command.CommandId,
            DateTimeOffset.UnixEpoch,
            command.EffectiveOptions,
            _dataDirectory);
        var document = VisionConfigurationStateCodec.Serialize(prototype, _dataDirectory);
        return _ledger.RegisterVisionConfiguration(new(
            command.CommandId,
            prototype.ConfigDigest,
            document,
            command.BundleUrl,
            command.BundleSha256,
            envelope.Nonce,
            ComputeEnvelopeBinding(envelope),
            _utcNow().ToUniversalTime()));
    }

    internal void RecordStructuralFailure(
        SignedCommand envelope,
        string? commandId,
        string code)
    {
        var binding = ComputeEnvelopeBinding(envelope);
        _ledger.RecordVisionConfigurationStructuralFailure(binding, commandId, code);
        _status.RecordStructuralFailure(code);
    }

    internal async Task RetryPendingAsync(CancellationToken ct)
    {
        if (!await _retryGate.WaitAsync(TimeSpan.Zero, ct).ConfigureAwait(false)) return;
        try
        {
            foreach (var item in _ledger.GetPendingVisionConfigurations(16))
            {
                ct.ThrowIfCancellationRequested();
                await ProcessOneAsync(item, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _retryGate.Release();
        }
    }

    private async Task ProcessOneAsync(
        VisionConfigurationOutboxItem item,
        CancellationToken ct)
    {
        var current = item;
        if (current.State == VisionConfigurationOutboxState.PendingApply)
        {
            if (!InstalledDataRootVerifier.IsSafe(_dataDirectory))
            {
                _logger.LogWarning(
                    "Vision configuration apply deferred: protected data root is untrusted");
                return;
            }
            var parsed = VisionConfigurationStateCodec.Parse(
                current.OptionsDocument,
                _dataDirectory);
            if (!parsed.IsValid || parsed.State is null ||
                !string.Equals(parsed.State.CommandId, current.CommandId, StringComparison.Ordinal) ||
                !string.Equals(parsed.State.ConfigDigest, current.ConfigDigest, StringComparison.Ordinal))
            {
                const string code = "vision_outbox_document_invalid";
                _ledger.RecordVisionConfigurationStructuralFailure(
                    current.EnvelopeBinding,
                    current.CommandId,
                    code);
                _status.RecordStructuralFailure(code);
                _logger.LogError(
                    "Vision configuration outbox document invalid commandId={CommandId}",
                    current.CommandId);
                return;
            }

            var cohortCode = "not_required";
            if (parsed.State.VisionOptions.Tesseract.Enabled)
            {
                if (current.BundleUrl is null || current.BundleSha256 is null ||
                    !string.Equals(
                        current.BundleSha256,
                        parsed.State.VisionOptions.Tesseract.BundleSha256,
                        StringComparison.Ordinal))
                {
                    const string code = "vision_outbox_bundle_binding_invalid";
                    _ledger.RecordVisionConfigurationStructuralFailure(
                        current.EnvelopeBinding,
                        current.CommandId,
                        code);
                    _status.RecordStructuralFailure(code);
                    return;
                }
                if (!_verifyPreinstalledCohort(parsed.State.VisionOptions))
                {
                    const string code = "vision_native_cohort_maintenance_required";
                    _ledger.RecordVisionConfigurationStructuralFailure(
                        current.EnvelopeBinding,
                        current.CommandId,
                        code);
                    if (!_ledger.MarkVisionConfigurationPendingAck(
                            current.CommandId,
                            current.ConfigDigest,
                            generation: null,
                            applySucceeded: false,
                            code))
                    {
                        _logger.LogWarning(
                            "Vision outbox maintenance-required transition lost race commandId={CommandId}",
                            current.CommandId);
                        return;
                    }
                    _status.RecordStructuralFailure(code);
                    current = current with
                    {
                        State = VisionConfigurationOutboxState.PendingAck,
                        ApplySucceeded = false,
                        Generation = null,
                        ResultCode = code,
                    };
                    _logger.LogWarning(
                        "Vision configuration requires privileged cohort repair commandId={CommandId}",
                        current.CommandId);
                }
                else
                {
                    cohortCode = "verified_preinstalled";
                }
            }

            // A maintenance-required transition goes straight to the durable
            // negative ACK below. Never write registry state that names
            // missing native executable bytes.
            if (current.State != VisionConfigurationOutboxState.PendingAck ||
                current.ApplySucceeded)
            {
                var applied = _coordinator.Apply(
                    current.CommandId,
                    parsed.State.VisionOptions);
                if (!applied.Succeeded || applied.State is null)
                {
                    _status.RecordStructuralFailure(applied.Code);
                    _logger.LogWarning(
                        "Vision registry apply deferred commandId={CommandId} code={Code}",
                        current.CommandId,
                        applied.Code);
                    return;
                }
                if (!_ledger.MarkVisionConfigurationPendingAck(
                        current.CommandId,
                        current.ConfigDigest,
                        applied.State.Generation,
                        applySucceeded: true,
                        cohortCode))
                {
                    _logger.LogWarning(
                        "Vision outbox pending-ACK transition lost race commandId={CommandId}",
                        current.CommandId);
                    return;
                }
                current = current with
                {
                    State = VisionConfigurationOutboxState.PendingAck,
                    ApplySucceeded = true,
                    Generation = applied.State.Generation,
                    ResultCode = cohortCode,
                };
            }
        }

        if (current.State != VisionConfigurationOutboxState.PendingAck)
            return;
        var ackOptions = ParseOptions(current);
        if (ackOptions is null)
        {
            const string code = "vision_outbox_ack_document_invalid";
            _ledger.RecordVisionConfigurationStructuralFailure(
                current.EnvelopeBinding,
                current.CommandId,
                code);
            _status.RecordStructuralFailure(code);
            return;
        }
        var telemetry = _status.Snapshot();
        var acked = current.ApplySucceeded
            ? await _ack(
                current.CommandId,
                true,
                new
                {
                    applied = true,
                    generation = current.Generation,
                    configDigest = current.ConfigDigest,
                    enabled = ackOptions.Enabled,
                    tesseractEnabled = ackOptions.Tesseract.Enabled,
                    cohort = current.ResultCode ?? "unknown",
                    status = telemetry.Status,
                    note = telemetry.Status == "active" ? "active" : "restart required to activate",
                },
                null,
                ct).ConfigureAwait(false)
            : await _ack(
                current.CommandId,
                false,
                new
                {
                    applied = false,
                    configDigest = current.ConfigDigest,
                    enabled = ackOptions.Enabled,
                    tesseractEnabled = ackOptions.Tesseract.Enabled,
                    maintenanceRequired = true,
                    status = telemetry.Status,
                },
                current.ResultCode ?? "vision_native_cohort_maintenance_required",
                ct).ConfigureAwait(false);
        if (acked)
            _ledger.MarkVisionConfigurationAcked(current.CommandId, current.ConfigDigest);
    }

    private VisionOptionsSnapshot? ParseOptions(VisionConfigurationOutboxItem item)
    {
        var parsed = VisionConfigurationStateCodec.Parse(item.OptionsDocument, _dataDirectory);
        return parsed.IsValid ? parsed.State?.VisionOptions : null;
    }

    internal static string ComputeEnvelopeBinding(SignedCommand envelope)
    {
        var canonical = SuavoAgent.Contracts.Maintenance.RemoteCommandTrust.BuildCommandCanonical(
            envelope.Command,
            envelope.AgentId,
            envelope.MachineFingerprint,
            envelope.Timestamp,
            envelope.Nonce,
            envelope.DataHash ?? string.Empty);
        var binding = string.Join('\n', canonical, envelope.KeyId, envelope.Signature);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)))
            .ToLowerInvariant();
    }
}
