using System.Runtime.Versioning;
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Fail-closed boundary for the retired generic PioneerRx IPC surface. Core
/// cannot submit arbitrary PMS click/type/query/writeback commands because
/// this surface has no independent cloud-signed, nonce-bound action plan.
/// Fixed, reviewed workflows use their dedicated workflow boundary instead.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PioneerRxCommandHandler
{
    private readonly ActuationGate _gate;

    public PioneerRxCommandHandler(
        ActuationGate gate,
        SendInputDriver driver,
        UiaLabelResolver resolver,
        ActuationConfig actuationConfig,
        PioneerRxConfig pioneerConfig,
        PioneerRxProcessTrustVerifier processTrust,
        ILogger logger)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(actuationConfig);
        ArgumentNullException.ThrowIfNull(pioneerConfig);
        ArgumentNullException.ThrowIfNull(processTrust);
        ArgumentNullException.ThrowIfNull(logger);
    }

    public Task<ActuationResult> HandleAsync(string command, JsonElement? data, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // This generic Core IPC surface has no independently cloud-signed, nonce-bound per-action plan.
        // A compromised Core would otherwise gain arbitrary PMS click/type power once the host receipt
        // exists. Keep every command unavailable; PricingWorkflow uses its separate fixed workflow path.
        if (command is PioneerRxActuationIpcCommands.Click or
            PioneerRxActuationIpcCommands.TypeText or
            PioneerRxActuationIpcCommands.Query or
            PioneerRxActuationIpcCommands.WritebackRxDelivery)
            return Task.FromResult(CapabilityUnavailable());

        return Task.FromResult(ActuationResult.Reject(
            ActuationRejectionCodes.MalformedRequest,
            "unknown PioneerRx actuation command",
            _gate.IsDryRun));
    }

    /// <summary>
    /// Pure, OS-independent BAA-scope gate (QA C4). True only when the request carries a non-empty
    /// <c>baaScopeTag</c> that is present in <paramref name="allowedScopes"/>. Fail-closed: a missing
    /// tag, a non-object/absent property, or an empty/unmatched allowlist all return false with a
    /// caller-facing <paramref name="rejectReason"/>.
    /// </summary>
    internal static bool IsBaaScopeAuthorized(
        JsonElement? data,
        IReadOnlySet<string> allowedScopes,
        out string rejectReason)
    {
        string? tag = null;
        if (data is { ValueKind: JsonValueKind.Object } d
            && d.TryGetProperty("baaScopeTag", out var t) && t.ValueKind == JsonValueKind.String)
        {
            tag = t.GetString();
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            rejectReason = "PioneerRx actuation rejected: request carries no BAA scope tag (fail closed)";
            return false;
        }

        if (allowedScopes is null || !allowedScopes.Contains(tag))
        {
            rejectReason = "PioneerRx actuation rejected: BAA scope is not authorized on this host";
            return false;
        }

        rejectReason = "";
        return true;
    }

    internal static ActuationResult CapabilityUnavailable() => ActuationResult.Reject(
        ActuationRejectionCodes.CapabilityUnavailable,
        "requested PioneerRx capability is not implemented on this agent",
        dryRun: false);
}
