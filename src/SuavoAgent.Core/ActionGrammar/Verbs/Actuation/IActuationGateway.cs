using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;

/// <summary>
/// Core→Helper actuation surface. Concrete production wiring lives in
/// <see cref="HelperActuationGateway"/> (named-pipe IPC). The interface
/// keeps verbs unit-testable without spinning a real pipe.
///
/// Every method maps 1:1 to a command in
/// <see cref="ActuationIpcCommands"/>. The Helper enforces the gate +
/// dry-run + PHI guards, so verbs never need to ask "is it safe to
/// actuate" — they ask the gateway and read the rejection envelope.
/// </summary>
public interface IActuationGateway
{
    Task<ActuationGateState> GetStateAsync(CancellationToken ct);
    Task<ActuationResult> ClickByLabelAsync(ClickByLabelRequest req, CancellationToken ct);
    Task<ActuationResult> ClickBySignatureAsync(ClickBySignatureRequest req, CancellationToken ct);
    Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct);
    Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct);
    Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct);

    /// <summary>Tell the Helper to re-read actuation.json and re-apply its app allowlist (no restart).
    /// Core persists the file + applies its own static BEFORE calling this, so both processes agree.</summary>
    Task<ActuationResult> ReloadAllowlistAsync(CancellationToken ct);

    /// <summary>Read a UIA element's value and assert it matches expected (read-only verification).
    /// Success = matched; Reject(element_not_found|assert_mismatch) otherwise. No raw value crosses
    /// the boundary — only pass/fail + a PHI-safe hint.</summary>
    Task<ActuationResult> AssertElementAsync(AssertElementRequest req, CancellationToken ct);

    /// <summary>Enumerate an allowlisted app's actionable UIA elements (read-only). The PHI-scrubbed
    /// inventory is returned as a JSON string in <see cref="ActuationResult.Payload"/>.</summary>
    Task<ActuationResult> DiscoverElementsAsync(DiscoverElementsRequest req, CancellationToken ct);
}
