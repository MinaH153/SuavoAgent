using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Events;

/// <summary>
/// HTTP-based event publisher. Signs each batch with the agent's API key
/// using the cloud-side verifyAgentRequest contract (`x-agent-api-key`,
/// `x-agent-timestamp`, `x-agent-signature` headers; signature =
/// HMAC-SHA256(apiKey, `${timestamp}:${body}`)). Buffers to
/// <see cref="LocalEventQueue"/> when network fails.
/// </summary>
/// <remarks>
/// Aligns with src/lib/agent-auth.ts in ~/Code/Suavo. Keep these in lockstep
/// or every event ingest fails HMAC verification.
/// </remarks>
public sealed class EventPublisher : IEventPublisher
{
    /// <summary>
    /// Wire format: snake_case field names (via <c>JsonPropertyName</c> on
    /// <see cref="StructuredEvent"/>) plus lowercase snake_case enum string
    /// values (via <see cref="JsonStringEnumConverter"/> with
    /// <see cref="JsonNamingPolicy.SnakeCaseLower"/>). Cloud Zod schema at
    /// <c>src/app/api/agent/events/ingest/route.ts</c> expects exactly this shape.
    /// </summary>
    internal static readonly JsonSerializerOptions SerializeOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false)
        }
    };

    private readonly HttpClient _http;
    private readonly ILogger<EventPublisher> _logger;
    private readonly LocalEventQueue _queue;
    private readonly Func<string> _apiKeyProvider;
    private readonly string _ingestUrl;

    /// <param name="apiKeyProvider">
    /// Delegate that returns the current agent API key string (same key used
    /// by HeartbeatWorker). Provider pattern lets rotation invalidate the
    /// key without replacing the publisher instance.
    /// </param>
    public EventPublisher(
        HttpClient http,
        ILogger<EventPublisher> logger,
        LocalEventQueue queue,
        Func<string> apiKeyProvider,
        string ingestUrl)
    {
        _http = http;
        _logger = logger;
        _queue = queue;
        _apiKeyProvider = apiKeyProvider;
        _ingestUrl = ingestUrl;
    }

    public int QueueDepth => _queue.Count();

    public void Publish(StructuredEvent evt) => _queue.Enqueue(evt);

    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        var batch = _queue.PeekBatch();
        if (batch.Count == 0) return 0;

        var bodyJson = JsonSerializer.Serialize(new IngestBatchPayload(batch), SerializeOptions);
        var apiKey = _apiKeyProvider();
        // Epoch-ms timestamp per verifyAgentRequest contract (accepts both
        // ISO 8601 and epoch-ms; epoch is cheaper on the wire).
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();

        var signature = ComputeSignature(apiKey, bodyJson, timestamp);

        using var request = new HttpRequestMessage(HttpMethod.Post, _ingestUrl)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-agent-api-key", apiKey);
        request.Headers.Add("x-agent-timestamp", timestamp);
        request.Headers.Add("x-agent-signature", signature);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Event ingest returned {Status}: {Body}. Leaving batch in local queue.",
                    (int)response.StatusCode, body);
                return 0;
            }

            var result = await response.Content.ReadFromJsonAsync<IngestResult>(cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                _logger.LogWarning("Event ingest returned success but no body — treating as failed for safety.");
                return 0;
            }

            var acceptedIds = batch.Select(e => e.Id).Take(result.AcceptedCount);
            _queue.Ack(acceptedIds);
            _logger.LogInformation(
                "Event ingest: accepted={Accepted} rejected={Rejected} queue_depth_after={Depth}",
                result.AcceptedCount, result.RejectedCount, _queue.Count());
            return result.AcceptedCount;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        {
            _logger.LogWarning(ex, "Event ingest network failure — batch remains queued locally.");
            return 0;
        }
    }

    /// <summary>
    /// HMAC-SHA256(apiKey, `${timestamp}:${body}`). Matches
    /// src/lib/agent-auth.ts in ~/Code/Suavo exactly — any deviation here
    /// means every event POST fails auth at the cloud.
    /// </summary>
    internal static string ComputeSignature(string apiKey, string bodyJson, string timestamp)
    {
        var keyBytes = Encoding.UTF8.GetBytes(apiKey);
        using var hmac = new HMACSHA256(keyBytes);
        var signingInput = $"{timestamp}:{bodyJson}";
        var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    private sealed record IngestBatchPayload(IReadOnlyList<StructuredEvent> Events);

    private sealed record IngestResult(int AcceptedCount, int RejectedCount);
}
