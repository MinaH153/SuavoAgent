using System.Runtime.Versioning;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Phase 5.3 — Helper-side dispatcher for the PioneerRx actuation IPC
/// commands. Reuses the existing gate / SendInputDriver / UiaLabelResolver
/// stack but enforces an additional <see cref="PioneerRxConfig.Enabled"/>
/// check on top of the sandbox-allowlist guard.
///
/// Configuration:
///   1. <see cref="ActuationGate"/> must be enabled (the same gate sandbox
///      verbs consult).
///   2. <see cref="PioneerRxConfig.Enabled"/> must be <c>true</c> — the
///      operator has to write the per-host opt-in file.
///   3. The request's <c>BaaScopeTag</c> must match what the verb declared
///      cloud-side. Deviations fail closed.
///
/// Click + type re-use the existing <see cref="UiaLabelResolver"/> against
/// the configured <see cref="PioneerRxConfig.ProcessName"/>. Query +
/// writeback are stubbed at this scaffold tier — they wire to the real
/// SQL adapter / WritebackProcessor once Phase 5.3 launches.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PioneerRxCommandHandler
{
    private readonly ActuationGate _gate;
    private readonly SendInputDriver _driver;
    private readonly UiaLabelResolver _resolver;
    private readonly ActuationConfig _actuationConfig;
    private readonly PioneerRxConfig _pioneerConfig;
    private readonly ILogger _logger;

    public PioneerRxCommandHandler(
        ActuationGate gate,
        SendInputDriver driver,
        UiaLabelResolver resolver,
        ActuationConfig actuationConfig,
        PioneerRxConfig pioneerConfig,
        ILogger logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _actuationConfig = actuationConfig ?? throw new ArgumentNullException(nameof(actuationConfig));
        _pioneerConfig = pioneerConfig ?? throw new ArgumentNullException(nameof(pioneerConfig));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<PioneerRxCommandHandler>();
    }

    public async Task<ActuationResult> HandleAsync(string command, JsonElement? data, CancellationToken ct)
    {
        if (!_pioneerConfig.Enabled)
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.GateDisabled,
                "PioneerRx actuation is disabled on this host (set pioneerrx.json Enabled=true to opt in)",
                _gate.IsDryRun);
        }

        try
        {
            return command switch
            {
                PioneerRxActuationIpcCommands.Click => await HandleClickAsync(data, ct).ConfigureAwait(false),
                PioneerRxActuationIpcCommands.TypeText => await HandleTypeAsync(data, ct).ConfigureAwait(false),
                PioneerRxActuationIpcCommands.Query => HandleQueryStub(data),
                PioneerRxActuationIpcCommands.WritebackRxDelivery => HandleWritebackStub(data),
                _ => ActuationResult.Reject(
                    ActuationRejectionCodes.MalformedRequest,
                    $"unknown PioneerRx command '{command}'",
                    _gate.IsDryRun),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "PioneerRxCommandHandler unexpected exception for {Command}", command);
            return ActuationResult.Reject(ActuationRejectionCodes.ExecutionException, ex.Message, _gate.IsDryRun);
        }
    }

    private async Task<ActuationResult> HandleClickAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun);
        var req = data.Value.Deserialize<PioneerRxClickRequest>();
        if (req is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun);
        if (string.IsNullOrWhiteSpace(req.Label)) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "label required", _gate.IsDryRun);

        var rejection = _gate.CheckOrReject();
        if (rejection is not null) return rejection;

        var mode = req.MatchMode == "contains_ci"
            ? UiaLabelResolver.MatchMode.ContainsCaseInsensitive
            : UiaLabelResolver.MatchMode.Exact;
        var timeout = req.TimeoutMs > 0
            ? TimeSpan.FromMilliseconds(req.TimeoutMs)
            : _actuationConfig.DefaultUiaTimeout;

        var resolved = _resolver.Resolve(req.Label, _pioneerConfig.ProcessName, mode, timeout);
        if (resolved is null)
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.LabelNotFound,
                $"label '{req.Label}' not found in '{_pioneerConfig.ProcessName}' within {(int)timeout.TotalMilliseconds}ms",
                _gate.IsDryRun);
        }
        // PioneerRxClickRequest does not yet carry a per-request DryRun (the
        // verb is a Phase-5.3 scaffold that synthesises dry-run cloud-side
        // and never reaches the Helper today). Pass false; the driver still
        // ORs with _gate.IsDryRun. Bug-21 follow-up: extend the PioneerRx
        // DTOs and verb when the live PMS click flow actually lands.
        return await _driver.ClickAtAsync(resolved.X, resolved.Y, dryRun: false, ct).ConfigureAwait(false);
    }

    private async Task<ActuationResult> HandleTypeAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun);
        var req = data.Value.Deserialize<PioneerRxTypeTextRequest>();
        if (req is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun);
        // Re-use the sandbox TypeText driver path — same SendInput, same PHI guard.
        return await _driver.TypeTextAsync(
            new TypeTextRequest(req.Text, req.ClearFirst, req.PerKeyDelayMs),
            ct).ConfigureAwait(false);
    }

    private ActuationResult HandleQueryStub(JsonElement? data)
    {
        // Phase 5.3 scaffold — the real SQL query path lands when the
        // writeback BAA amendment is signed. Until then we audit the
        // attempted query without actually hitting the PMS DB.
        _logger.Information("PioneerRx query (scaffold-no-op): payload size={Size}", data?.GetRawText().Length ?? 0);
        return ActuationResult.Success(0, dryRun: true, evidenceHash: SendInputDriver.ComputeEvidenceHash("pioneerrx_query", data?.GetRawText() ?? "{}"));
    }

    private ActuationResult HandleWritebackStub(JsonElement? data)
    {
        _logger.Information("PioneerRx writeback (scaffold-no-op): payload size={Size}", data?.GetRawText().Length ?? 0);
        return ActuationResult.Success(0, dryRun: true, evidenceHash: SendInputDriver.ComputeEvidenceHash("pioneerrx_writeback", data?.GetRawText() ?? "{}"));
    }
}
