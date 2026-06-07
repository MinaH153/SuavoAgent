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
                ActuationIpcCommands.ReloadAllowlist => HandleReloadAllowlist(),
                ActuationIpcCommands.AssertElement => await HandleAssertElementAsync(data, ct).ConfigureAwait(false),
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

    private async Task<ActuationResult> HandleTypeTextAsync(JsonElement? data, CancellationToken ct)
    {
        if (data is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "missing data", _gate.IsDryRun);
        var req = data.Value.Deserialize<TypeTextRequest>();
        if (req is null) return ActuationResult.Reject(ActuationRejectionCodes.MalformedRequest, "deserialise failed", _gate.IsDryRun);

        var result = await _driver.TypeTextAsync(req, ct).ConfigureAwait(false);

        // SELF-VERIFICATION (read-back): after a LIVE successful type, confirm the keystrokes actually
        // landed by reading the focused field's value via UIA. This converts the type verb from "trust
        // the SendInput return" to "prove the text is in the field" — it catches the dropped-char /
        // wrong-control silent failures that previously required a human to eyeball the screen.
        //
        // Fail-GRACEFUL: only fail when we positively read a value that does NOT contain the typed text.
        // A null read-back (no focus, control exposes no value, UIA hiccup) is treated as UNVERIFIED and
        // does NOT fail an otherwise-successful type — never a false rejection.
        if (result.Ok && !result.DryRun && !string.IsNullOrEmpty(req.Text))
        {
            try
            {
                // Retry the read-back: a control can commit the text slightly after SendInput returns, so
                // a single read could miss it. Compare NORMALISED (alphanumeric, lower-cased) so fields
                // that reformat input — phone masks, currency, case-normalisation, trimming — do NOT
                // false-fail (Codex review). Verified as soon as a normalised read contains the normalised
                // typed text.
                var typedNorm = NormalizeForVerification(req.Text);
                var verified = false;
                var sawNonEmptyValue = false; // read a field value that had real content
                var sawEmptyValue = false;    // read a field value that was effectively empty
                for (var i = 0; i < 6 && !verified; i++)
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    var readback = _resolver.ReadFocusedElementValue();
                    if (readback is null) continue; // no readable VALUE this tick
                    var readNorm = NormalizeForVerification(readback);
                    if (readNorm.Length == 0) { sawEmptyValue = true; continue; }
                    sawNonEmptyValue = true;
                    if (typedNorm.Length == 0 || readNorm.Contains(typedNorm, StringComparison.Ordinal))
                        verified = true;
                }

                if (verified)
                {
                    _logger.Information("TypeText verified: focused field reflects the typed text on read-back");
                }
                else if (!sawNonEmptyValue && sawEmptyValue)
                {
                    // Unambiguous failure: we read the focused field and it was EMPTY after a substantive
                    // type — the keystrokes did not land (dropped / wrong control). A field that merely
                    // REFORMATS input is never empty, so this can't false-fail it. Fail closed. Lengths
                    // only — NEVER log the typed text or field contents (PHI).
                    _logger.Warning(
                        "TypeText verification FAILED: focused field read back EMPTY after a non-empty type (typedLen={Typed})",
                        req.Text.Length);
                    return ActuationResult.Reject(
                        ActuationRejectionCodes.TypeNotVerified,
                        "focused field is empty on UIA read-back after typing — keystrokes did not land",
                        result.DryRun);
                }
                else
                {
                    // Either no readable value at all, OR a non-empty value that didn't match the
                    // normalised typed text (a reformatting field, or a partial/append we can't confirm).
                    // Do NOT fail an otherwise-successful type — degrade to UNVERIFIED.
                    _logger.Information(
                        "TypeText UNVERIFIED: read-back did not confirm the typed text (no value, or a value we can't match) — type allowed");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TypeText read-back verification errored — treating as unverified");
            }
        }

        return result;
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

        var allowedProcesses = ActuationAllowlistedSandboxApps.ProcessNames.Values
            .Concat(ActuationAllowlistedSandboxApps.ProcessNames.Keys);
        if (!allowedProcesses.Contains(req.ProcessName, StringComparer.OrdinalIgnoreCase))
            return Task.FromResult(ActuationResult.Reject(
                ActuationRejectionCodes.ProcessNotAllowed,
                $"processName '{req.ProcessName}' not allowed for sandbox actuation",
                effectiveDryRun));

        // Dry-run: the actuation that would have produced the asserted state never fired — pass (no-op).
        if (effectiveDryRun)
            return Task.FromResult(ActuationResult.Success(0, dryRun: true, evidenceHash: "assert_element_dryrun"));

        var timeout = req.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(req.TimeoutMs) : _config.DefaultUiaTimeout;
        var read = _resolver.ReadElementValue(req.ProcessName, req.AutomationId, req.Name, req.ControlType, timeout);
        if (read is null)
        {
            var locator = req.AutomationId ?? req.Name ?? req.ControlType ?? "?";
            return Task.FromResult(ActuationResult.Reject(
                ActuationRejectionCodes.ElementNotFound,
                $"element (locator '{locator}') not found or no readable value in '{req.ProcessName}' within {(int)timeout.TotalMilliseconds}ms",
                effectiveDryRun));
        }

        var matched = req.MatchMode switch
        {
            "exact" => string.Equals(read, req.Expected, StringComparison.Ordinal),
            "contains_ci" => read.Contains(req.Expected, StringComparison.OrdinalIgnoreCase),
            _ => NormalizeForVerification(read).Contains(NormalizeForVerification(req.Expected), StringComparison.Ordinal), // normalized (default)
        };

        if (matched)
        {
            _logger.Information("AssertElement PASS: element value matches expected (mode={Mode}, expectedLen={Len})", req.MatchMode, req.Expected.Length);
            return Task.FromResult(ActuationResult.Success(0, dryRun: false, evidenceHash: "assert_element_pass"));
        }

        // Mismatch — lengths + mode ONLY; never the raw read value or expected text (PHI on a PMS box).
        _logger.Warning(
            "AssertElement MISMATCH: mode={Mode} expectedLen={Exp} actualLen={Act}",
            req.MatchMode, req.Expected.Length, read.Length);
        return Task.FromResult(ActuationResult.Reject(
            ActuationRejectionCodes.AssertMismatch,
            $"element value did not match expected (mode={req.MatchMode}, expectedLen={req.Expected.Length}, actualLen={read.Length})",
            effectiveDryRun));
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

    /// <summary>
    /// Re-apply the actuation app allowlist in THIS (Helper) process from actuation.json on disk — Core
    /// has already persisted the merged AllowedApps and applied it in its own static; this makes the
    /// Helper's AUTHORITATIVE allowlist agree, with no restart. The file is the single source of truth;
    /// LoadAndExtendFromConfig re-reads it (defaults always retained, malformed entries dropped).
    /// </summary>
    private ActuationResult HandleReloadAllowlist()
    {
        ActuationAllowlistedSandboxApps.LoadAndExtendFromConfig();
        var count = ActuationAllowlistedSandboxApps.ProcessNames.Count;
        _logger.Information("Actuation allowlist reloaded from config — {Count} app(s) now allowed", count);
        return ActuationResult.Success(0, _gate.IsDryRun, evidenceHash: "reload_allowlist");
    }
}
