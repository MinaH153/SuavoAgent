using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Companion;

public interface IAutopilotControlClient
{
    Task<bool> PauseAsync(CancellationToken cancellationToken);
    Task<bool> ResumeAsync(CancellationToken cancellationToken);
    Task<bool> StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Sends fixed-vocabulary local human controls over the already authenticated
/// Helper-to-Core pipe. Resume is bound to Core's current generation so stale
/// messages cannot reopen a newer pause/stop state.
/// </summary>
public sealed class CoreAutopilotControlClient : IAutopilotControlClient
{
    private readonly Func<string, string?, CancellationToken, Task<IpcResponse?>> _send;
    private readonly ILogger _logger;

    public CoreAutopilotControlClient(IpcPipeClient ipc, ILogger logger)
        : this(
            (ipc ?? throw new ArgumentNullException(nameof(ipc))).TrySendAsync,
            logger)
    {
    }

    internal CoreAutopilotControlClient(
        Func<string, string?, CancellationToken, Task<IpcResponse?>> send,
        ILogger logger)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger)))
            .ForContext<CoreAutopilotControlClient>();
    }

    public Task<bool> PauseAsync(CancellationToken cancellationToken) =>
        SendControlAsync(
            "pause",
            expectedGeneration: null,
            expectedPaused: true,
            expectedStopped: false,
            cancellationToken);

    public async Task<bool> ResumeAsync(CancellationToken cancellationToken)
    {
        var state = await _send(
            IpcCommands.GetAutopilotControlState,
            null,
            cancellationToken).ConfigureAwait(false);
        if (state?.Status != IpcStatus.Ok || state.Data is not { } data
            || !data.TryGetProperty("ControlGeneration", out var generationElement)
            || !generationElement.TryGetInt64(out var generation)
            || !TryReadBoolean(data, "Paused", out var paused)
            || !TryReadBoolean(data, "Stopped", out var stopped)
            || stopped)
        {
            _logger.Warning("Autopilot resume refused: Core control state is unavailable or stopped");
            return false;
        }

        // A pharmacist-input pause exists only in Helper. If Core proves it is
        // already open and not stopped, that exact state is the acknowledgement
        // needed to clear the local transient pause; no synthetic Core resume is
        // required. Companion-origin pauses still take the generation-bound path.
        if (!paused) return true;

        return await SendControlAsync(
                "resume",
                generation,
                expectedPaused: false,
                expectedStopped: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<bool> StopAsync(CancellationToken cancellationToken) =>
        SendControlAsync(
            "stop",
            expectedGeneration: null,
            expectedPaused: true,
            expectedStopped: true,
            cancellationToken);

    private async Task<bool> SendControlAsync(
        string action,
        long? expectedGeneration,
        bool expectedPaused,
        bool expectedStopped,
        CancellationToken cancellationToken)
    {
        var request = new AutopilotControlRequest(
            AutopilotControlRequest.CurrentContractVersion,
            action,
            "companion_control",
            expectedGeneration);
        var response = await _send(
            IpcCommands.AutopilotControl,
            JsonSerializer.Serialize(request),
            cancellationToken).ConfigureAwait(false);
        if (response?.Status != IpcStatus.Ok || response.Data is not { } data
            || !TryReadBoolean(data, "Applied", out var applied)
            || !TryReadBoolean(data, "Paused", out var paused)
            || !TryReadBoolean(data, "Stopped", out var stopped)
            || !data.TryGetProperty("Action", out var actionElement)
            || !ActionMatches(actionElement, action)
            || !applied || paused != expectedPaused || stopped != expectedStopped)
        {
            _logger.Warning("Autopilot {Action} was not acknowledged by Core", action);
            return false;
        }

        return true;
    }

    private static bool TryReadBoolean(
        JsonElement parent,
        string property,
        out bool value)
    {
        value = false;
        if (!parent.TryGetProperty(property, out var element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool ActionMatches(JsonElement element, string action)
    {
        if (element.ValueKind == JsonValueKind.String)
            return string.Equals(element.GetString(), action, StringComparison.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var number))
            return false;
        return number == (action switch
        {
            "pause" => 0,
            "resume" => 1,
            "stop" => 2,
            _ => -1,
        });
    }
}
