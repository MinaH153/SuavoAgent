using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Ipc;

namespace SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;

/// <summary>
/// Production <see cref="IActuationGateway"/> — sends typed actuation
/// requests over the existing Core→Helper named pipe (the same pipe used
/// for pricing lookups and intent cursor).
///
/// Connection lifecycle: per-call. We don't keep a persistent connection
/// because IPC is a hot path for pricing jobs and we don't want lock
/// contention; for actuation a 50-200ms connect overhead is invisible.
///
/// Failure semantics: returns a synthetic <see cref="ActuationResult"/>
/// with rejection code "ipc_unavailable" if the Helper isn't running, so
/// the caller (verb) can record the rejection in the audit chain
/// uniformly with all other rejection paths.
/// </summary>
public sealed class HelperActuationGateway : IActuationGateway
{
    private readonly Func<IIpcCommandClient> _clientFactory;
    private readonly ILogger<HelperActuationGateway> _logger;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _commandTimeout;

    public HelperActuationGateway(
        Func<IIpcCommandClient> clientFactory,
        ILogger<HelperActuationGateway> logger,
        TimeSpan? connectTimeout = null,
        TimeSpan? commandTimeout = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
        _commandTimeout = commandTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<ActuationGateState> GetStateAsync(CancellationToken ct)
    {
        var client = _clientFactory();
        try
        {
            if (!await client.ConnectAsync(_connectTimeout, ct).ConfigureAwait(false))
            {
                return new ActuationGateState(
                    Enabled: false,
                    DryRun: true,
                    PausedUntilUtc: null,
                    PauseReason: "helper_unavailable",
                    KillSwitchTrippedUtc: null);
            }

            var resp = await client.SendAsync(
                new IpcRequest(Guid.NewGuid().ToString("N"), ActuationIpcCommands.GetState, 1, null),
                _commandTimeout,
                ct).ConfigureAwait(false);

            if (resp is { Status: 200, Data: { } d } && d.ValueKind == JsonValueKind.Object)
            {
                return d.Deserialize<ActuationGateState>() ?? FailedState();
            }
            return FailedState();
        }
        finally
        {
            if (client is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<ActuationResult> ClickByLabelAsync(ClickByLabelRequest req, CancellationToken ct) =>
        SendAsync(ActuationIpcCommands.ClickByLabel, req, ct);

    public Task<ActuationResult> ClickBySignatureAsync(ClickBySignatureRequest req, CancellationToken ct) =>
        SendAsync(ActuationIpcCommands.ClickBySignature, req, ct);

    public Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct) =>
        SendAsync(ActuationIpcCommands.TypeText, req, ct);

    public Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct) =>
        SendAsync(ActuationIpcCommands.PressKeys, req, ct);

    public Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct) =>
        SendAsync(ActuationIpcCommands.LaunchSandboxApp, req, ct);

    public Task<ActuationResult> ReloadAllowlistAsync(CancellationToken ct) =>
        SendAsync(ActuationIpcCommands.ReloadAllowlist, new ReloadAllowlistRequest(), ct);

    public Task<ActuationResult> AssertElementAsync(AssertElementRequest req, CancellationToken ct) =>
        SendAsync(ActuationIpcCommands.AssertElement, req, ct);

    public Task<ActuationResult> DiscoverElementsAsync(DiscoverElementsRequest req, CancellationToken ct) =>
        SendAsync(ActuationIpcCommands.DiscoverElements, req, ct);

    private async Task<ActuationResult> SendAsync<TReq>(string command, TReq req, CancellationToken ct)
    {
        var client = _clientFactory();
        try
        {
            if (!await client.ConnectAsync(_connectTimeout, ct).ConfigureAwait(false))
            {
                _logger.LogWarning("HelperActuationGateway: cannot connect to Helper for {Command}", command);
                return ActuationResult.Reject("ipc_unavailable", "Helper is not running or pipe is busy", dryRun: true);
            }

            var dataJson = JsonSerializer.SerializeToElement(req);
            var resp = await client.SendAsync(
                new IpcRequest(Guid.NewGuid().ToString("N"), command, 1, dataJson),
                _commandTimeout,
                ct).ConfigureAwait(false);

            if (resp is null)
            {
                return ActuationResult.Reject("ipc_no_response", "Helper did not respond within timeout", dryRun: true);
            }
            if (resp.Status != 200)
            {
                return ActuationResult.Reject(
                    resp.Error?.Code ?? "ipc_error",
                    "Helper rejected the actuation request",
                    dryRun: true);
            }
            if (resp.Data is null) return ActuationResult.Reject("ipc_empty_payload", "Helper returned empty payload", dryRun: true);
            return resp.Data.Value.Deserialize<ActuationResult>()
                ?? ActuationResult.Reject("ipc_payload_deserialise_failed", "could not deserialise ActuationResult", dryRun: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // IPC exception messages can include mutable pipe, executable, or
            // desktop context. Keep both logs and audit-facing rejection text
            // structural and PHI-free.
            _logger.LogWarning(
                "HelperActuationGateway: channel exception for {Command} ({ErrorType})",
                command,
                ex.GetType().Name);
            return ActuationResult.Reject(
                "ipc_exception",
                "Helper actuation channel failed",
                dryRun: true);
        }
        finally
        {
            if (client is IAsyncDisposable ad) await ad.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ActuationGateState FailedState() => new(
        Enabled: false,
        DryRun: true,
        PausedUntilUtc: null,
        PauseReason: "helper_unreachable",
        KillSwitchTrippedUtc: null);
}
