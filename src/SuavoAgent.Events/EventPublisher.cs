using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Events;

/// <summary>
/// HTTP-based event publisher. Signs each batch with the agent's HMAC-SHA256
/// key; cloud verifies before ingest. Buffers to <see cref="LocalEventQueue"/>
/// when network fails.
/// </summary>
public sealed class EventPublisher : IEventPublisher
{
    private readonly HttpClient _http;
    private readonly ILogger<EventPublisher> _logger;
    private readonly LocalEventQueue _queue;
    private readonly Func<byte[]> _signingKeyProvider;
    private readonly string _ingestUrl;
    private readonly string _pharmacyHeaderValue;

    /// <param name="signingKeyProvider">
    /// Delegate that returns the current HMAC-SHA256 signing key bytes.
    /// Provider pattern lets rotation invalidate the key without replacing
    /// the publisher instance.
    /// </param>
    public EventPublisher(
        HttpClient http,
        ILogger<EventPublisher> logger,
        LocalEventQueue queue,
        Func<byte[]> signingKeyProvider,
        string ingestUrl,
        string pharmacyHeaderValue)
    {
        _http = http;
        _logger = logger;
        _queue = queue;
        _signingKeyProvider = signingKeyProvider;
        _ingestUrl = ingestUrl;
        _pharmacyHeaderValue = pharmacyHeaderValue;
    }

    public int QueueDepth => _queue.Count();

    public void Publish(StructuredEvent evt) => _queue.Enqueue(evt);

    public async Task<int> FlushAsync(CancellationToken cancellationToken = default)
    {
        var batch = _queue.PeekBatch();
        if (batch.Count == 0) return 0;

        var bodyJson = JsonSerializer.Serialize(new IngestBatchPayload(batch));
        var bodyBytes = Encoding.UTF8.GetBytes(bodyJson);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var signature = ComputeSignature(bodyBytes, timestamp);

        using var request = new HttpRequestMessage(HttpMethod.Post, _ingestUrl)
        {
            Content = new ByteArrayContent(bodyBytes)
            {
                Headers = { { "Content-Type", "application/json" } }
            }
        };
        request.Headers.Add("X-Pharmacy-Id", _pharmacyHeaderValue);
        request.Headers.Add("X-Timestamp", timestamp);
        request.Headers.Add("X-Signature", signature);

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

    internal string ComputeSignature(byte[] bodyBytes, string timestamp)
    {
        var keyBytes = _signingKeyProvider();
        using var hmac = new HMACSHA256(keyBytes);
        var tsBytes = Encoding.UTF8.GetBytes(timestamp);
        var buffer = new byte[bodyBytes.Length + tsBytes.Length];
        Buffer.BlockCopy(bodyBytes, 0, buffer, 0, bodyBytes.Length);
        Buffer.BlockCopy(tsBytes, 0, buffer, bodyBytes.Length, tsBytes.Length);
        var mac = hmac.ComputeHash(buffer);
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    private sealed record IngestBatchPayload(IReadOnlyList<StructuredEvent> Events);

    private sealed record IngestResult(int AcceptedCount, int RejectedCount);
}
