using System.Runtime.Versioning;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Glue between the IPC server and the actuation primitives. Each
/// <c>actuation.*</c> request lands here; we deserialise into the typed
/// contract from <see cref="ActuationContracts"/>, run the gate-aware
/// driver, return the <see cref="ActuationResult"/> envelope.
///
/// Stays free of Win32 details — those live in
/// <see cref="SendInputDriver"/> / <see cref="UiaLabelResolver"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ActuationCommandHandler
{
    private readonly ActuationGate _gate;
    private readonly SendInputDriver _driver;
    private readonly UiaLabelResolver _resolver;
    private readonly UiaSignatureResolver _signatureResolver;
    private readonly ActuationConfig _config;
    private readonly ILogger _logger;

    public ActuationCommandHandler(
        ActuationGate gate,
        SendInputDriver driver,
        UiaLabelResolver resolver,
        ActuationConfig config,
        ILogger logger,
        UiaSignatureResolver? signatureResolver = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<ActuationCommandHandler>();
        _signatureResolver = signatureResolver ?? new UiaSignatureResolver(logger);
    }

    public ActuationGateState GetState() => _gate.Snapshot();

    public async Task<ActuationResult> HandleAsync(string command, JsonElement? data, CancellationToken ct)
    {
        try
        {
            return command switch
            {
                ActuationIpcCommands.ClickByLabel => await HandleClickByLabelAsync(data, ct).ConfigureAwait(false),
                ActuationIpcCommands.ClickBySignature => await HandleClickBySignatureAsync(data, ct).ConfigureAwait(false),
                ActuationIpcCommands.TypeText => await HandleTypeTextAsync(data, ct).ConfigureAwait(false),
                ActuationIpcCommands.PressKeys => await HandlePressKeysAsync(data, ct).ConfigureAwait(false),
                ActuationIpcCommands.LaunchSandboxApp => await HandleLaunchSandboxAppAsync(data, ct).ConfigureAwait(false),
                _ => ActuationResult.Reject(
                    ActuationRejectionCodes.MalformedRequest,
                    $"unknown actuation command '{command}'",
                    _gate.IsDryRun),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ActuationCommandHandler unexpected exception for {Command}", command);
            return ActuationResult.Reject(ActuationRejectionCodes.ExecutionException, ex.Message, _gate.IsDryRun);
        }
    }

    private async Task<ActuationResult> HandleClickByLabelAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun);
        var req = data.Value.Deserialize<ClickByLabelRequest>();
        if (req is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun);

        // Bug 21 effective-OR: as soon as we know the request's dryRun bit,
        // every rejection from this handler reports the EFFECTIVE state, not
        // just the gate's. Otherwise a request that asked for dry-run could
        // be rejected with dryRun=false in the audit row.
        var effectiveDryRun = req.DryRun || _gate.IsDryRun;

        if (string.IsNullOrWhiteSpace(req.Label) || string.IsNullOrWhiteSpace(req.ProcessName))
            return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "label/processName required", effectiveDryRun);

        var allowedProcesses = ActuationAllowlistedSandboxApps.ProcessNames.Values
            .Concat(ActuationAllowlistedSandboxApps.ProcessNames.Keys);
        if (!allowedProcesses.Contains(req.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessNotAllowed,
                $"processName '{req.ProcessName}' not allowed for sandbox actuation",
                effectiveDryRun);
        }

        var rejection = _gate.CheckOrReject();
        if (rejection is not null) return rejection with { DryRun = effectiveDryRun };

        var mode = req.MatchMode switch
        {
            "contains_ci" => UiaLabelResolver.MatchMode.ContainsCaseInsensitive,
            _ => UiaLabelResolver.MatchMode.Exact,
        };
        var timeout = req.TimeoutMs > 0
            ? TimeSpan.FromMilliseconds(req.TimeoutMs)
            : _config.DefaultUiaTimeout;

        var resolved = _resolver.Resolve(req.Label, req.ProcessName, mode, timeout);
        if (resolved is null)
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.LabelNotFound,
                $"label '{req.Label}' not found in '{req.ProcessName}' within {(int)timeout.TotalMilliseconds}ms",
                effectiveDryRun);
        }

        return await _driver.ClickAtAsync(resolved.X, resolved.Y, req.DryRun, ct).ConfigureAwait(false);
    }

    private async Task<ActuationResult> HandleClickBySignatureAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun);
        var req = data.Value.Deserialize<ClickBySignatureRequest>();
        if (req is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun);

        var effectiveDryRun = req.DryRun || _gate.IsDryRun;

        if (string.IsNullOrWhiteSpace(req.AutomationId) || string.IsNullOrWhiteSpace(req.ControlType) || string.IsNullOrWhiteSpace(req.ProcessName))
            return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "controlType/automationId/processName required", effectiveDryRun);

        var allowedProcesses = ActuationAllowlistedSandboxApps.ProcessNames.Values
            .Concat(ActuationAllowlistedSandboxApps.ProcessNames.Keys);
        if (!allowedProcesses.Contains(req.ProcessName, StringComparer.OrdinalIgnoreCase))
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessNotAllowed,
                $"processName '{req.ProcessName}' not allowed for sandbox actuation",
                effectiveDryRun);
        }

        var rejection = _gate.CheckOrReject();
        if (rejection is not null) return rejection with { DryRun = effectiveDryRun };

        var timeout = req.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(req.TimeoutMs) : _config.DefaultUiaTimeout;

        var resolved = _signatureResolver.Resolve(req.ControlType, req.AutomationId, req.ClassName, req.ProcessName, timeout);
        if (resolved is null)
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.LabelNotFound,
                $"signature ({req.ControlType}|{req.AutomationId}) not found in '{req.ProcessName}' within {(int)timeout.TotalMilliseconds}ms",
                effectiveDryRun);
        }

        return await _driver.ClickAtAsync(resolved.X, resolved.Y, req.DryRun, ct).ConfigureAwait(false);
    }

    private Task<ActuationResult> HandleTypeTextAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun));
        var req = data.Value.Deserialize<TypeTextRequest>();
        if (req is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun));
        return _driver.TypeTextAsync(req, ct);
    }

    private Task<ActuationResult> HandlePressKeysAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun));
        var req = data.Value.Deserialize<PressKeysRequest>();
        if (req is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun));
        return _driver.PressKeysAsync(req, ct);
    }

    private Task<ActuationResult> HandleLaunchSandboxAppAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun));
        var req = data.Value.Deserialize<LaunchSandboxAppRequest>();
        if (req is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun));
        return _driver.LaunchSandboxAppAsync(req, ct);
    }
}
