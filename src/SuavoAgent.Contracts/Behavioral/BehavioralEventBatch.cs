using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Behavioral;

public static class BehavioralEventChannels
{
    public const string Pms = "pms";
    public const string System = "system";

    public static bool IsKnown(string channel) =>
        string.Equals(channel, Pms, StringComparison.Ordinal)
        || string.Equals(channel, System, StringComparison.Ordinal);
}

/// <summary>
/// Versioned, retry-safe delivery envelope for PHI-free behavioral events.
/// A Helper keeps one stream id for the lifetime of each observer buffer and
/// reuses the same batch id until Core acknowledges the batch.
/// </summary>
public sealed record BehavioralEventBatch
{
    public const int LegacyContractVersion = 1;
    public const int CurrentContractVersion = 2;
    public const int MaximumEventCount = 200;

    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = CurrentContractVersion;

    [JsonPropertyName("batchId")]
    public required string BatchId { get; init; }

    [JsonPropertyName("streamId")]
    public required string StreamId { get; init; }

    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    [JsonPropertyName("firstSequence")]
    public long FirstSequence { get; init; }

    [JsonPropertyName("lastSequence")]
    public long LastSequence { get; init; }

    /// <summary>
    /// Cumulative count of events evicted by this stream before this envelope
    /// was created. Cumulative truth makes retries idempotent.
    /// </summary>
    [JsonPropertyName("droppedTotal")]
    public long DroppedTotal { get; init; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonPropertyName("leaseId")]
    public string? LeaseId { get; init; }

    [JsonPropertyName("leaseSessionBinding")]
    public string? LeaseSessionBinding { get; init; }

    [JsonPropertyName("leaseEpoch")]
    public long LeaseEpoch { get; init; }

    /// <summary>
    /// HMAC-SHA256 over the complete envelope (excluding this property),
    /// keyed by the short-lived observation lease. This prevents a batch
    /// from being rebound to another lease/session after capture.
    /// </summary>
    [JsonPropertyName("authenticationTag")]
    public string? AuthenticationTag { get; init; }

    [JsonPropertyName("events")]
    public required IReadOnlyList<BehavioralEvent> Events { get; init; }
}

/// <summary>Core's explicit durable-acceptance acknowledgement.</summary>
public sealed record BehavioralEventBatchAck
{
    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = BehavioralEventBatch.CurrentContractVersion;

    [JsonPropertyName("batchId")]
    public required string BatchId { get; init; }

    [JsonPropertyName("streamId")]
    public required string StreamId { get; init; }

    [JsonPropertyName("acceptedThroughSequence")]
    public long AcceptedThroughSequence { get; init; }

    [JsonPropertyName("eventsStored")]
    public int EventsStored { get; init; }

    [JsonPropertyName("eventsRejected")]
    public int EventsRejected { get; init; }

    [JsonPropertyName("duplicate")]
    public bool Duplicate { get; init; }
}

/// <summary>
/// Short-lived Core-issued key lease. SessionBinding is deliberately opaque;
/// Helper never receives the learning-session identifier.
/// </summary>
public sealed record ObservationKeyLease
{
    public const int CurrentContractVersion = 1;

    [JsonPropertyName("contractVersion")]
    public int ContractVersion { get; init; } = CurrentContractVersion;

    [JsonPropertyName("leaseId")]
    public required string LeaseId { get; init; }

    [JsonPropertyName("sessionBinding")]
    public required string SessionBinding { get; init; }

    [JsonPropertyName("epoch")]
    public long Epoch { get; init; }

    [JsonPropertyName("issuedAtUtc")]
    public DateTimeOffset IssuedAtUtc { get; init; }

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset ExpiresAtUtc { get; init; }

    [JsonPropertyName("keyMaterial")]
    public required string KeyMaterial { get; init; }
}

public sealed record ObservationKeyLeaseRequest
{
    [JsonPropertyName("currentLeaseId")]
    public string? CurrentLeaseId { get; init; }
}

public static class ObservationBatchAuthentication
{
    public static BehavioralEventBatch Seal(
        BehavioralEventBatch batch,
        ObservationKeyLease lease)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.ContractVersion != ObservationKeyLease.CurrentContractVersion)
            throw new InvalidDataException("unsupported_observation_lease_version");

        var bound = batch with
        {
            ContractVersion = BehavioralEventBatch.CurrentContractVersion,
            LeaseId = lease.LeaseId,
            LeaseSessionBinding = lease.SessionBinding,
            LeaseEpoch = lease.Epoch,
            AuthenticationTag = null,
        };
        return bound with { AuthenticationTag = ComputeTag(bound, lease.KeyMaterial) };
    }

    public static bool Verify(BehavioralEventBatch batch, string keyMaterial)
    {
        if (string.IsNullOrWhiteSpace(batch.AuthenticationTag)) return false;
        try
        {
            var expected = Convert.FromBase64String(ComputeTag(
                batch with { AuthenticationTag = null }, keyMaterial));
            var actual = Convert.FromBase64String(batch.AuthenticationTag);
            return expected.Length == actual.Length
                && CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string ComputeTag(BehavioralEventBatch batch, string keyMaterial)
    {
        var key = Convert.FromBase64String(keyMaterial);
        if (key.Length < 32)
            throw new InvalidDataException("observation_lease_key_too_short");

        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            batch.ContractVersion,
            batch.BatchId,
            batch.StreamId,
            batch.Channel,
            batch.FirstSequence,
            batch.LastSequence,
            batch.DroppedTotal,
            batch.CreatedAtUtc,
            batch.LeaseId,
            batch.LeaseSessionBinding,
            batch.LeaseEpoch,
            batch.Events,
        });
        try
        {
            return Convert.ToBase64String(HMACSHA256.HashData(key, payload));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}

public enum BehavioralBatchDeliveryDisposition
{
    Retry = 0,
    Acknowledged = 1,
    Quarantine = 2,
}

public sealed record BehavioralBatchDeliveryResult(
    BehavioralBatchDeliveryDisposition Disposition,
    string? ReasonCode = null)
{
    public static readonly BehavioralBatchDeliveryResult Acknowledged =
        new(BehavioralBatchDeliveryDisposition.Acknowledged);
    public static readonly BehavioralBatchDeliveryResult Retry =
        new(BehavioralBatchDeliveryDisposition.Retry);

    public static BehavioralBatchDeliveryResult Quarantine(string reasonCode) =>
        new(BehavioralBatchDeliveryDisposition.Quarantine, reasonCode);
}

public sealed record QuarantinedBehavioralBatch(
    BehavioralEventBatch Batch,
    string ReasonCode,
    DateTimeOffset QuarantinedAtUtc);

/// <summary>Complete encrypted-spool snapshot; never write this as plaintext.</summary>
public sealed record BehavioralEventBufferState
{
    public const int CurrentContractVersion = 1;

    public int ContractVersion { get; init; } = CurrentContractVersion;
    public required string StreamId { get; init; }
    public required string Channel { get; init; }
    public long LastAssignedSequence { get; init; }
    public long DroppedTotal { get; init; }
    public long DroppedSinceFlush { get; init; }
    public long DeliveredBatches { get; init; }
    public long DeliveryFailures { get; init; }
    public long LastDeliveredSequence { get; init; }
    public DateTimeOffset? LastDeliveryUtc { get; init; }
    public DateTimeOffset? LastFailureUtc { get; init; }
    public ObservationKeyLease? ActiveLease { get; init; }
    public IReadOnlyList<BehavioralEvent> QueuedEvents { get; init; } = [];
    public BehavioralEventBatch? InFlight { get; init; }
    public IReadOnlyList<QuarantinedBehavioralBatch> QuarantinedBatches { get; init; } = [];
}

public interface IBehavioralEventSpool : IDisposable
{
    BehavioralEventBufferState? Load();
    void Save(BehavioralEventBufferState state);
}

public sealed class BehavioralEventPersistenceException : Exception
{
    public BehavioralEventPersistenceException(string code, Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Point-in-time, PHI-free delivery health for one Helper stream.</summary>
public sealed record BehavioralBufferTelemetry(
    string StreamId,
    string Channel,
    int QueuedEvents,
    int InFlightEvents,
    long DroppedEvents,
    long DeliveredBatches,
    long DeliveryFailures,
    long LastDeliveredSequence,
    DateTimeOffset? LastDeliveryUtc,
    DateTimeOffset? LastFailureUtc,
    int QuarantinedBatches = 0,
    bool PersistenceHealthy = true,
    string? PersistenceFaultCode = null,
    long? LeaseEpoch = null,
    DateTimeOffset? LeaseExpiresAtUtc = null);
