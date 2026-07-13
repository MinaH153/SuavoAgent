using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Behavioral;

namespace SuavoAgent.Core.Ipc;

/// <summary>Strict IPC boundary for versioned observation envelopes.</summary>
public static class BehavioralBatchIpcProcessor
{
    public static IpcResponse Process(
        IpcRequest request,
        string expectedChannel,
        BehavioralEventReceiver receiver,
        EventRateLimiter rateLimiter)
    {
        if (!request.Data.HasValue)
            return Reject(request, "missing_batch", "observation batch is required", retryable: false);

        try
        {
            var root = request.Data.Value;
            if (root.ValueKind == JsonValueKind.Array)
                return ProcessLegacy(request, root, expectedChannel, receiver, rateLimiter);
            if (root.ValueKind != JsonValueKind.Object)
                return Reject(request, "invalid_batch", "observation batch must be an object", false);

            var batch = JsonSerializer.Deserialize<BehavioralEventBatch>(root.GetRawText());
            if (batch is null)
                return Reject(request, "invalid_batch", "observation batch is invalid", false);
            if (!string.Equals(batch.Channel, expectedChannel, StringComparison.Ordinal))
                return Reject(request, "channel_command_mismatch", "observation channel does not match command", false);
            if (batch.Events is null || batch.Events.Count == 0)
                return Reject(request, "empty_batch", "observation batch is empty", false);
            if (batch.Events.Count > BehavioralEventBatch.MaximumEventCount)
                return Reject(request, "batch_too_large", "observation batch exceeds the event limit", false);
            if (!rateLimiter.TryAcquire(batch.Events.Count))
                return Reject(request, "rate_limited", "observation event rate exceeded", retryable: true);

            var result = receiver.ProcessBatch(batch);
            if (!result.Accepted)
                return Reject(request, result.ErrorCode ?? "invalid_batch", "observation batch rejected", false);

            var ack = new BehavioralEventBatchAck
            {
                ContractVersion = batch.ContractVersion,
                BatchId = batch.BatchId,
                StreamId = batch.StreamId,
                AcceptedThroughSequence = result.AcceptedThroughSequence,
                EventsStored = result.EventsStored,
                EventsRejected = result.EventsRejected,
                Duplicate = result.Duplicate,
            };
            return new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(ack),
                null);
        }
        catch (JsonException)
        {
            return Reject(request, "invalid_json", "observation batch JSON is invalid", false);
        }
    }

    private static IpcResponse ProcessLegacy(
        IpcRequest request,
        JsonElement root,
        string expectedChannel,
        BehavioralEventReceiver receiver,
        EventRateLimiter rateLimiter)
    {
        var events = JsonSerializer.Deserialize<List<BehavioralEvent>>(root.GetRawText());
        if (events is null || events.Count == 0)
            return Reject(request, "empty_batch", "observation batch is empty", false);
        if (events.Count > BehavioralEventBatch.MaximumEventCount)
            return Reject(request, "batch_too_large", "observation batch exceeds the event limit", false);
        if (!rateLimiter.TryAcquire(events.Count))
            return Reject(request, "rate_limited", "observation event rate exceeded", true);

        receiver.ProcessBatch(events, droppedSinceLast: 0, sourceChannel: expectedChannel);
        return new IpcResponse(request.Id, IpcStatus.Ok, request.Command, null, null);
    }

    private static IpcResponse Reject(
        IpcRequest request,
        string code,
        string message,
        bool retryable) =>
        new(
            request.Id,
            IpcStatus.BadRequest,
            request.Command,
            null,
            new IpcError(code, message, retryable, 0));
}
