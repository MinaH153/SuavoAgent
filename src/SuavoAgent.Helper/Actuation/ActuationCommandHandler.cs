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
    // Visual-only narration bubble. Fire-and-forget; null when presence isn't wired.
    private readonly SuavoAgent.Helper.Presence.PresenceController? _presence;

    public ActuationCommandHandler(
        ActuationGate gate,
        SendInputDriver driver,
        UiaLabelResolver resolver,
        ActuationConfig config,
        ILogger logger,
        UiaSignatureResolver? signatureResolver = null,
        SuavoAgent.Helper.Presence.PresenceController? presence = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<ActuationCommandHandler>();
        _signatureResolver = signatureResolver ?? new UiaSignatureResolver(logger);
        _presence = presence;
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
                ActuationIpcCommands.ReloadAllowlist => HandleReloadAllowlist(),
                ActuationIpcCommands.AssertElement => await HandleAssertElementAsync(data, ct).ConfigureAwait(false),
                ActuationIpcCommands.DiscoverElements => HandleDiscoverElements(data),
                _ => ActuationResult.Reject(
                    ActuationRejectionCodes.MalformedRequest,
                    "unknown actuation command",
                    _gate.IsDryRun),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            _logger.Warning("ActuationCommandHandler failed locally");
            return ActuationResult.Reject(
                ActuationRejectionCodes.ExecutionException,
                "actuation command failed locally",
                _gate.IsDryRun);
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

        if (!ActuationAllowlistedSandboxApps.IsDeclaredSandboxProcess(req.ProcessName))
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessNotAllowed,
                "requested process is not an approved sandbox target",
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

        var sawUntrustedProcess = false;
        bool TrustProcess(int pid)
        {
            var verdict = SandboxProcessTrustVerifier.VerifyResolvedProcess(pid, req.ProcessName);
            if (!verdict.Trusted) sawUntrustedProcess = true;
            return verdict.Trusted;
        }

        var resolved = _resolver.Resolve(req.Label, req.ProcessName, mode, timeout, TrustProcess);
        if (resolved is null)
        {
            _presence?.Narrate("Couldn't find", "approved target", SuavoAgent.Helper.Presence.PresenceTones.Confirm);
            if (sawUntrustedProcess)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "resolved process failed sandbox path/publisher identity verification",
                    effectiveDryRun);
            }
            return ActuationResult.Reject(
                ActuationRejectionCodes.LabelNotFound,
                "requested UI target was not found in the approved sandbox app",
                effectiveDryRun);
        }

        _presence?.Narrate("Clicking", "approved target");
        return await _driver.ClickAtAsync(
            resolved.X, resolved.Y, req.DryRun, ct, resolved.Pid, req.ProcessName,
            SendInputDriver.TargetTrustKind.Sandbox).ConfigureAwait(false);
    }

    private async Task<ActuationResult> HandleClickBySignatureAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun);
        var req = data.Value.Deserialize<ClickBySignatureRequest>();
        if (req is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun);

        var effectiveDryRun = req.DryRun || _gate.IsDryRun;

        if (string.IsNullOrWhiteSpace(req.AutomationId) || string.IsNullOrWhiteSpace(req.ControlType) || string.IsNullOrWhiteSpace(req.ProcessName))
            return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "controlType/automationId/processName required", effectiveDryRun);

        if (!ActuationAllowlistedSandboxApps.IsDeclaredSandboxProcess(req.ProcessName))
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessNotAllowed,
                "requested process is not an approved sandbox target",
                effectiveDryRun);
        }

        var rejection = _gate.CheckOrReject();
        if (rejection is not null) return rejection with { DryRun = effectiveDryRun };

        var timeout = req.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(req.TimeoutMs) : _config.DefaultUiaTimeout;

        var sawUntrustedProcess = false;
        bool TrustProcess(int pid)
        {
            var verdict = SandboxProcessTrustVerifier.VerifyResolvedProcess(pid, req.ProcessName);
            if (!verdict.Trusted) sawUntrustedProcess = true;
            return verdict.Trusted;
        }

        var resolved = _signatureResolver.Resolve(
            req.ControlType, req.AutomationId, req.ClassName, req.ProcessName, timeout, TrustProcess);
        if (resolved is null)
        {
            _presence?.Narrate("Couldn't find", "approved target", SuavoAgent.Helper.Presence.PresenceTones.Confirm);
            if (sawUntrustedProcess)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "resolved process failed sandbox path/publisher identity verification",
                    effectiveDryRun);
            }
            return ActuationResult.Reject(
                ActuationRejectionCodes.LabelNotFound,
                "requested structural UI target was not found in the approved sandbox app",
                effectiveDryRun);
        }

        _presence?.Narrate("Clicking", "approved target");
        return await _driver.ClickAtAsync(
            resolved.X, resolved.Y, req.DryRun, ct, resolved.Pid, req.ProcessName,
            SendInputDriver.TargetTrustKind.Sandbox).ConfigureAwait(false);
    }

    private async Task<ActuationResult> HandleTypeTextAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun);
        var req = data.Value.Deserialize<TypeTextRequest>();
        if (req is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun);
        if (!ActuationAllowlistedSandboxApps.IsDeclaredSandboxProcess(req.ProcessName))
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessNotAllowed,
                "processName is required and must identify a declared sandbox app",
                req.DryRun || _gate.IsDryRun);
        }

        _presence?.Narrate("Typing", null); // NEVER the text — PHI
        return await _driver.TypeTextAsync(
            req, ct, SendInputDriver.TargetTrustKind.Sandbox).ConfigureAwait(false);
    }

    /// <summary>
    /// Reduce a string to lower-cased alphanumerics for type read-back comparison, so a field that
    /// reformats input (punctuation/spacing in phone masks, currency, dates; case changes) is still
    /// recognised as containing what was typed instead of false-failing verification.
    /// </summary>
    private static string NormalizeForVerification(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// Read a UIA element's value and assert it matches <c>expected</c> — the verification keystone.
    /// READ-ONLY: injects no input, so it does not consult the actuation gate's pause/kill (a read can
    /// never fight the user). It DOES honour dry-run: a dry-run workflow's prior actuation never
    /// happened, so asserting live state is meaningless → short-circuit to a pass. PHI-safe: the raw
    /// read value is NEVER logged or returned — a mismatch reports only normalised lengths + the mode.
    /// </summary>
    private Task<ActuationResult> HandleAssertElementAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun));
        var req = data.Value.Deserialize<AssertElementRequest>();
        if (req is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun));

        var effectiveDryRun = req.DryRun || _gate.IsDryRun;

        if (string.IsNullOrWhiteSpace(req.ProcessName) || string.IsNullOrEmpty(req.Expected))
            return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "processName/expected required", effectiveDryRun));

        var hasLocator = !string.IsNullOrWhiteSpace(req.AutomationId)
            || !string.IsNullOrWhiteSpace(req.Name)
            || !string.IsNullOrWhiteSpace(req.ControlType);
        if (!hasLocator)
            return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "one of automationId/name/controlType required", effectiveDryRun));

        if (!ActuationAllowlistedSandboxApps.IsDeclaredSandboxProcess(req.ProcessName))
            return Task.FromResult(ActuationResult.Reject(
                ActuationRejectionCodes.ProcessNotAllowed,
                "requested process is not an approved sandbox target",
                effectiveDryRun));

        // Dry-run: the actuation that would have produced the asserted state never fired — pass (no-op).
        if (effectiveDryRun)
            return Task.FromResult(ActuationResult.Success(0, dryRun: true, evidenceHash: "assert_element_dryrun"));

        var timeout = req.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(req.TimeoutMs) : _config.DefaultUiaTimeout;
        var sawUntrustedProcess = false;
        bool TrustProcess(int pid)
        {
            var verdict = SandboxProcessTrustVerifier.VerifyResolvedProcess(pid, req.ProcessName);
            if (!verdict.Trusted) sawUntrustedProcess = true;
            return verdict.Trusted;
        }
        var read = _resolver.ReadElementValue(
            req.ProcessName, req.AutomationId, req.Name, req.ControlType, timeout, TrustProcess);
        if (read is null)
        {
            if (sawUntrustedProcess)
            {
                return Task.FromResult(ActuationResult.Reject(
                    ActuationRejectionCodes.ProcessIdentityUntrusted,
                    "resolved process failed sandbox path/publisher identity verification",
                    effectiveDryRun));
            }
            return Task.FromResult(ActuationResult.Reject(
                ActuationRejectionCodes.ElementNotFound,
                "requested UI element was unavailable or unreadable in the approved sandbox app",
                effectiveDryRun));
        }

        var safeMatchMode = req.MatchMode switch
        {
            "exact" => "exact",
            "contains_ci" => "contains_ci",
            _ => "normalized",
        };
        var matched = safeMatchMode switch
        {
            "exact" => string.Equals(read, req.Expected, StringComparison.Ordinal),
            "contains_ci" => read.Contains(req.Expected, StringComparison.OrdinalIgnoreCase),
            _ => NormalizeForVerification(read).Contains(NormalizeForVerification(req.Expected), StringComparison.Ordinal), // normalized (default)
        };

        if (matched)
        {
            _logger.Information("AssertElement PASS: element value matches expected (mode={Mode}, expectedLen={Len})", safeMatchMode, req.Expected.Length);
            return Task.FromResult(ActuationResult.Success(0, dryRun: false, evidenceHash: "assert_element_pass"));
        }

        // Mismatch — lengths + mode ONLY; never the raw read value or expected text (PHI on a PMS box).
        _logger.Warning(
            "AssertElement MISMATCH: mode={Mode} expectedLen={Exp} actualLen={Act}",
            safeMatchMode, req.Expected.Length, read.Length);
        return Task.FromResult(ActuationResult.Reject(
            ActuationRejectionCodes.AssertMismatch,
            $"element value did not match expected (mode={safeMatchMode}, expectedLen={req.Expected.Length}, actualLen={read.Length})",
            effectiveDryRun));
    }

    /// <summary>
    /// Enumerate an allowlisted app's actionable UIA elements (controlType + automationId +
    /// PHI-scrubbed name) into the result Payload — the agent's "look at the UI" capability. READ-ONLY:
    /// no gate/dry-run interaction (reading the tree injects nothing). Names are PHI-scrubbed in the
    /// resolver before they reach the payload.
    /// </summary>
    private ActuationResult HandleDiscoverElements(JsonElement? data)
    {
        if (data is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun);
        var req = data.Value.Deserialize<DiscoverElementsRequest>();
        if (req is null || string.IsNullOrWhiteSpace(req.ProcessName))
            return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "processName required", _gate.IsDryRun);

        if (!ActuationAllowlistedSandboxApps.IsDeclaredSandboxProcess(req.ProcessName))
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessNotAllowed,
                "requested process is not an approved sandbox target",
                _gate.IsDryRun);

        var sawUntrustedProcess = false;
        bool TrustProcess(int pid)
        {
            var verdict = SandboxProcessTrustVerifier.VerifyResolvedProcess(pid, req.ProcessName);
            if (!verdict.Trusted) sawUntrustedProcess = true;
            return verdict.Trusted;
        }
        var elements = _resolver.DiscoverElements(req.ProcessName, req.Max, TrustProcess);
        if (elements.Count == 0 && sawUntrustedProcess)
        {
            return ActuationResult.Reject(
                ActuationRejectionCodes.ProcessIdentityUntrusted,
                "resolved process failed sandbox path/publisher identity verification",
                _gate.IsDryRun);
        }
        var payload = JsonSerializer.Serialize(elements);
        _logger.Information("DiscoverElements returned {Count} structural elements", elements.Count);
        return ActuationResult.SuccessWithPayload(0, _gate.IsDryRun, evidenceHash: "discover_elements", payload);
    }

    private Task<ActuationResult> HandlePressKeysAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun));
        var req = data.Value.Deserialize<PressKeysRequest>();
        if (req is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun));
        if (!ActuationAllowlistedSandboxApps.IsDeclaredSandboxProcess(req.ProcessName))
        {
            return Task.FromResult(ActuationResult.Reject(
                ActuationRejectionCodes.ProcessNotAllowed,
                "processName is required and must identify a declared sandbox app",
                req.DryRun || _gate.IsDryRun));
        }
        _presence?.Narrate("Pressing keys", null);
        return _driver.PressKeysAsync(req, ct, SendInputDriver.TargetTrustKind.Sandbox);
    }

    private Task<ActuationResult> HandleLaunchSandboxAppAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun));
        var req = data.Value.Deserialize<LaunchSandboxAppRequest>();
        if (req is null) return Task.FromResult(ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun));
        return _driver.LaunchSandboxAppAsync(req, ct);
    }

    /// <summary>
    /// Remote callers cannot widen local sandbox authority. The immutable local policy remains unchanged.
    /// </summary>
    private ActuationResult HandleReloadAllowlist()
    {
        _logger.Warning("Remote sandbox policy mutation was rejected");
        return ActuationResult.Reject(
            ActuationRejectionCodes.RemotePolicyMutationDenied,
            "remote sandbox policy mutation is not permitted",
            _gate.IsDryRun);
    }
}
