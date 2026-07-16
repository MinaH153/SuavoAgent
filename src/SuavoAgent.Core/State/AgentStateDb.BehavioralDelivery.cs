using Microsoft.Data.Sqlite;
using SuavoAgent.Contracts.Behavioral;
using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    public sealed record BehavioralDeliveryCommit(
        bool Duplicate,
        long AcceptedThroughSequence,
        int EventsStored,
        int EventsRejected,
        long SequenceGap,
        long DroppedDelta,
        IReadOnlySet<long> StoredSourceSequences);

    public sealed record BehavioralDeliveryHealth(
        int StreamCount,
        int PmsStreamCount,
        int SystemStreamCount,
        long DroppedEventCount,
        long SequenceGapCount,
        long AcceptedBatchCount,
        long VerifiedBatchCount,
        long DuplicateBatchCount,
        long RejectedEventCount,
        DateTimeOffset? LastBatchUtc,
        DateTimeOffset? LastPmsBatchUtc,
        DateTimeOffset? LastSystemBatchUtc,
        DateTimeOffset? LastVerifiedBatchUtc,
        DateTimeOffset? LastVerifiedPmsBatchUtc,
        DateTimeOffset? LastVerifiedSystemBatchUtc,
        bool LegacyUnverifiedSeen,
        DateTimeOffset? LastLegacyBatchUtc,
        string ObservationSpoolStatus,
        DateTimeOffset? LastObservationSpoolStatusUtc);

    public sealed record ObserverStatusSnapshot(string Status, DateTimeOffset ReceivedAtUtc);

    public sealed record ObservationLeaseValidation(
        bool IsValid,
        string? SessionId,
        string? ErrorCode,
        DateTimeOffset? ExpiresAtUtc);

    public const string PreLearningObservationSession = "pre-learning";

    private void InitializeBehavioralDeliverySchema()
    {
        TryAlter("ALTER TABLE behavioral_events ADD COLUMN source_stream_id TEXT");
        TryAlter("ALTER TABLE behavioral_events ADD COLUMN source_sequence_num INTEGER");
        TryAlter("ALTER TABLE behavioral_events ADD COLUMN source_channel TEXT NOT NULL DEFAULT 'pms'");

        using var command = _conn.CreateCommand();
        command.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS idx_be_source_stream_seq
                ON behavioral_events(source_stream_id, source_sequence_num)
                WHERE source_stream_id IS NOT NULL AND source_sequence_num IS NOT NULL;

            CREATE TABLE IF NOT EXISTS behavioral_delivery_streams (
                stream_id TEXT PRIMARY KEY,
                channel TEXT NOT NULL,
                last_sequence INTEGER NOT NULL,
                dropped_total INTEGER NOT NULL,
                sequence_gap_total INTEGER NOT NULL DEFAULT 0,
                accepted_batch_count INTEGER NOT NULL DEFAULT 0,
                duplicate_batch_count INTEGER NOT NULL DEFAULT 0,
                rejected_event_count INTEGER NOT NULL DEFAULT 0,
                last_batch_id TEXT NOT NULL,
                last_session_id TEXT NOT NULL,
                last_received_at TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS behavioral_delivery_health (
                singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                legacy_unverified_seen INTEGER NOT NULL DEFAULT 0,
                last_legacy_received_at TEXT,
                observation_spool_status TEXT NOT NULL DEFAULT 'not_reported',
                last_observation_spool_status_at TEXT
            );
            INSERT OR IGNORE INTO behavioral_delivery_health(singleton_id, legacy_unverified_seen)
            VALUES (1, 0);

            CREATE TABLE IF NOT EXISTS behavioral_delivery_session_metrics (
                stream_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                channel TEXT NOT NULL,
                dropped_event_count INTEGER NOT NULL DEFAULT 0,
                sequence_gap_count INTEGER NOT NULL DEFAULT 0,
                accepted_batch_count INTEGER NOT NULL DEFAULT 0,
                verified_batch_count INTEGER NOT NULL DEFAULT 0,
                duplicate_batch_count INTEGER NOT NULL DEFAULT 0,
                rejected_event_count INTEGER NOT NULL DEFAULT 0,
                last_received_at TEXT NOT NULL,
                last_verified_received_at TEXT,
                PRIMARY KEY (stream_id, session_id)
            );

            CREATE TABLE IF NOT EXISTS observation_key_leases (
                lease_id TEXT PRIMARY KEY,
                session_binding TEXT NOT NULL,
                lease_epoch INTEGER NOT NULL UNIQUE,
                session_id TEXT NOT NULL,
                key_digest TEXT NOT NULL,
                issued_at TEXT NOT NULL,
                expires_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_observation_key_leases_session
                ON observation_key_leases(session_id, lease_epoch DESC);
            """;
        command.ExecuteNonQuery();
        TryAlter("ALTER TABLE behavioral_delivery_health ADD COLUMN last_legacy_received_at TEXT");
        TryAlter("ALTER TABLE behavioral_delivery_health ADD COLUMN observation_spool_status TEXT NOT NULL DEFAULT 'not_reported'");
        TryAlter("ALTER TABLE behavioral_delivery_health ADD COLUMN last_observation_spool_status_at TEXT");
        TryAlter("ALTER TABLE behavioral_delivery_session_metrics ADD COLUMN verified_batch_count INTEGER NOT NULL DEFAULT 0");
        TryAlter("ALTER TABLE behavioral_delivery_session_metrics ADD COLUMN last_verified_received_at TEXT");
    }

    public ObservationKeyLease IssueObservationKeyLease(
        string? sessionId,
        DateTimeOffset issuedAtUtc,
        TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        sessionId = string.IsNullOrWhiteSpace(sessionId)
            ? PreLearningObservationSession
            : sessionId;

        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var masterSalt = GetObservationMasterSaltLocked(sessionId, transaction);
            var leaseId = Base64Url(RandomNumberGenerator.GetBytes(24));
            var epoch = ReadNextObservationLeaseEpoch(transaction);
            var masterBytes = Convert.FromBase64String(masterSalt);
            var bindingContext = Encoding.UTF8.GetBytes("observation-session-v1\0" + sessionId);
            string sessionBinding;
            try
            {
                sessionBinding = Base64Url(HMACSHA256.HashData(masterBytes, bindingContext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(masterBytes);
                CryptographicOperations.ZeroMemory(bindingContext);
            }
            var keyMaterial = DeriveObservationLeaseKey(
                masterSalt,
                leaseId,
                sessionBinding,
                epoch);
            var expiresAtUtc = issuedAtUtc + lifetime;

            using var insert = _conn.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO observation_key_leases
                    (lease_id, session_binding, lease_epoch, session_id,
                     key_digest, issued_at, expires_at)
                VALUES
                    (@lease, @binding, @epoch, @session,
                     @digest, @issued, @expires)
                """;
            insert.Parameters.AddWithValue("@lease", leaseId);
            insert.Parameters.AddWithValue("@binding", sessionBinding);
            insert.Parameters.AddWithValue("@epoch", epoch);
            insert.Parameters.AddWithValue("@session", sessionId);
            insert.Parameters.AddWithValue(
                "@digest",
                Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(keyMaterial)))
                    .ToLowerInvariant());
            insert.Parameters.AddWithValue("@issued", issuedAtUtc.ToString("o"));
            insert.Parameters.AddWithValue("@expires", expiresAtUtc.ToString("o"));
            insert.ExecuteNonQuery();
            transaction.Commit();

            return new ObservationKeyLease
            {
                LeaseId = leaseId,
                SessionBinding = sessionBinding,
                Epoch = epoch,
                IssuedAtUtc = issuedAtUtc,
                ExpiresAtUtc = expiresAtUtc,
                KeyMaterial = keyMaterial,
            };
        }
    }

    public ObservationKeyLease GetOrIssueObservationKeyLease(
        string? sessionId,
        string? currentLeaseId,
        DateTimeOffset nowUtc,
        TimeSpan lifetime,
        TimeSpan minimumRemaining)
    {
        sessionId = string.IsNullOrWhiteSpace(sessionId)
            ? PreLearningObservationSession
            : sessionId;
        if (!string.IsNullOrWhiteSpace(currentLeaseId))
        {
            lock (_connLock)
            {
                using var command = _conn.CreateCommand();
                command.CommandText = """
                    SELECT session_binding, lease_epoch, session_id, issued_at, expires_at
                    FROM observation_key_leases
                    WHERE lease_id = @lease
                    """;
                command.Parameters.AddWithValue("@lease", currentLeaseId);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    var binding = reader.GetString(0);
                    var epoch = reader.GetInt64(1);
                    var mappedSession = reader.GetString(2);
                    var issuedAt = DateTimeOffset.Parse(reader.GetString(3));
                    var expiresAt = DateTimeOffset.Parse(reader.GetString(4));
                    reader.Close();
                    if (string.Equals(mappedSession, sessionId, StringComparison.Ordinal)
                        && expiresAt - nowUtc > minimumRemaining)
                    {
                        var masterSalt = GetObservationMasterSaltLocked(mappedSession, transaction: null);
                        return new ObservationKeyLease
                        {
                            LeaseId = currentLeaseId,
                            SessionBinding = binding,
                            Epoch = epoch,
                            IssuedAtUtc = issuedAt,
                            ExpiresAtUtc = expiresAt,
                            KeyMaterial = DeriveObservationLeaseKey(
                                masterSalt,
                                currentLeaseId,
                                binding,
                                epoch),
                        };
                    }
                }
            }
        }

        return IssueObservationKeyLease(sessionId, nowUtc, lifetime);
    }

    public ObservationLeaseValidation ValidateObservationLease(
        BehavioralEventBatch batch,
        DateTimeOffset nowUtc)
    {
        if (batch.ContractVersion != BehavioralEventBatch.CurrentContractVersion)
            return new ObservationLeaseValidation(false, null, "observation_lease_required", null);
        if (string.IsNullOrWhiteSpace(batch.LeaseId)
            || string.IsNullOrWhiteSpace(batch.LeaseSessionBinding)
            || batch.LeaseEpoch <= 0)
        {
            return new ObservationLeaseValidation(false, null, "observation_lease_invalid", null);
        }

        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT session_binding, lease_epoch, session_id,
                       key_digest, expires_at
                FROM observation_key_leases
                WHERE lease_id = @lease
                """;
            command.Parameters.AddWithValue("@lease", batch.LeaseId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return new ObservationLeaseValidation(false, null, "observation_lease_unknown", null);

            var sessionBinding = reader.GetString(0);
            var epoch = reader.GetInt64(1);
            var sessionId = reader.GetString(2);
            var keyDigest = reader.GetString(3);
            var expiresAtUtc = DateTimeOffset.Parse(reader.GetString(4));
            reader.Close();
            if (!string.Equals(sessionBinding, batch.LeaseSessionBinding, StringComparison.Ordinal)
                || epoch != batch.LeaseEpoch)
            {
                return new ObservationLeaseValidation(
                    false,
                    null,
                    "observation_lease_binding_mismatch",
                    expiresAtUtc);
            }
            if (nowUtc >= expiresAtUtc)
            {
                return new ObservationLeaseValidation(
                    false,
                    sessionId,
                    "observation_lease_expired",
                    expiresAtUtc);
            }

            var masterSalt = GetObservationMasterSaltLocked(sessionId, transaction: null);
            var keyMaterial = DeriveObservationLeaseKey(
                masterSalt,
                batch.LeaseId,
                sessionBinding,
                epoch);
            var actualDigest = Convert.ToHexString(
                    SHA256.HashData(Convert.FromBase64String(keyMaterial)))
                .ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualDigest),
                    Encoding.ASCII.GetBytes(keyDigest))
                || !ObservationBatchAuthentication.Verify(batch, keyMaterial))
            {
                return new ObservationLeaseValidation(
                    false,
                    null,
                    "observation_batch_authentication_failed",
                    expiresAtUtc);
            }

            return new ObservationLeaseValidation(true, sessionId, null, expiresAtUtc);
        }
    }

    public BehavioralDeliveryCommit CommitBehavioralDeliveryBatch(
        string sessionId,
        BehavioralEventBatch batch,
        IReadOnlyList<BehavioralEvent> eventsToStore,
        int rejectedEvents)
    {
        lock (_connLock)
        {
            using var transaction = _conn.BeginTransaction(System.Data.IsolationLevel.Serializable);

            var state = ReadBehavioralDeliveryState(batch.StreamId, transaction);
            if (state is not null
                && !string.Equals(state.Value.Channel, batch.Channel, StringComparison.Ordinal))
            {
                throw BehavioralDeliveryContractException.StreamChannelMismatch();
            }

            var previousSequence = state?.LastSequence ?? 0;
            var previousDropped = state?.DroppedTotal ?? 0;

            if (batch.LastSequence <= previousSequence)
            {
                using var duplicate = _conn.CreateCommand();
                duplicate.Transaction = transaction;
                duplicate.CommandText = """
                    UPDATE behavioral_delivery_streams
                    SET duplicate_batch_count = duplicate_batch_count + 1,
                        last_received_at = @received
                    WHERE stream_id = @stream
                    """;
                duplicate.Parameters.AddWithValue("@received", DateTimeOffset.UtcNow.ToString("o"));
                duplicate.Parameters.AddWithValue("@stream", batch.StreamId);
                duplicate.ExecuteNonQuery();
                IncrementBehavioralDuplicateMetric(
                    batch.StreamId,
                    state?.LastSessionId ?? sessionId,
                    batch.Channel,
                    transaction);
                transaction.Commit();

                return new BehavioralDeliveryCommit(
                    Duplicate: true,
                    AcceptedThroughSequence: previousSequence,
                    EventsStored: 0,
                    EventsRejected: 0,
                    SequenceGap: 0,
                    DroppedDelta: 0,
                    StoredSourceSequences: new HashSet<long>());
            }

            if (previousSequence > 0 && batch.FirstSequence <= previousSequence)
                throw BehavioralDeliveryContractException.StreamPartialOverlap();
            if (batch.DroppedTotal < previousDropped)
                throw BehavioralDeliveryContractException.DroppedTotalRegression();

            var expectedNext = previousSequence + 1;
            var sequenceGap = Math.Max(0, batch.FirstSequence - expectedNext);
            var droppedDelta = Math.Max(0, batch.DroppedTotal - previousDropped);
            var nextLocalSequence = ReadNextBehavioralSequence(sessionId, transaction);
            var storedSequences = new HashSet<long>();

            foreach (var behavioralEvent in eventsToStore)
            {
                if (behavioralEvent.Seq <= previousSequence)
                    continue;

                using var insert = _conn.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR IGNORE INTO behavioral_events
                        (session_id, sequence_num, event_type, event_subtype, tree_hash,
                         element_id, element_control_type, element_class_name, element_name_hash,
                         element_bounding_rect, keystroke_category, keystroke_timing_bucket,
                         keystroke_sequence_count, occurrence_count, helper_timestamp, received_at,
                         source_stream_id, source_sequence_num, source_channel)
                    VALUES
                        (@sid, @seq, @type, @subtype, @treeHash,
                         @elemId, @ctrlType, @className, @nameHash,
                         @boundRect, @ksCat, @ksBucket,
                         @ksCount, @occCount, @helperTs, @received,
                         @sourceStream, @sourceSeq, @sourceChannel)
                    """;
                insert.Parameters.AddWithValue("@sid", sessionId);
                insert.Parameters.AddWithValue("@seq", nextLocalSequence);
                insert.Parameters.AddWithValue("@type", behavioralEvent.Type.ToString().ToLowerInvariant());
                insert.Parameters.AddWithValue("@subtype", (object?)behavioralEvent.Subtype ?? DBNull.Value);
                insert.Parameters.AddWithValue("@treeHash", (object?)behavioralEvent.TreeHash ?? DBNull.Value);
                insert.Parameters.AddWithValue("@elemId", (object?)behavioralEvent.ElementId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@ctrlType", (object?)behavioralEvent.ControlType ?? DBNull.Value);
                insert.Parameters.AddWithValue("@className", (object?)behavioralEvent.ClassName ?? DBNull.Value);
                insert.Parameters.AddWithValue("@nameHash", (object?)behavioralEvent.NameHash ?? DBNull.Value);
                insert.Parameters.AddWithValue("@boundRect", (object?)behavioralEvent.BoundingRect ?? DBNull.Value);
                insert.Parameters.AddWithValue("@ksCat", (object?)behavioralEvent.KeystrokeCat?.ToString().ToLowerInvariant() ?? DBNull.Value);
                insert.Parameters.AddWithValue("@ksBucket", (object?)behavioralEvent.Timing?.ToString().ToLowerInvariant() ?? DBNull.Value);
                insert.Parameters.AddWithValue("@ksCount", (object?)behavioralEvent.KeystrokeCount ?? DBNull.Value);
                insert.Parameters.AddWithValue("@occCount", behavioralEvent.OccurrenceCount);
                insert.Parameters.AddWithValue("@helperTs", behavioralEvent.Timestamp.ToString("o"));
                insert.Parameters.AddWithValue("@received", DateTimeOffset.UtcNow.ToString("o"));
                insert.Parameters.AddWithValue("@sourceStream", batch.StreamId);
                insert.Parameters.AddWithValue("@sourceSeq", behavioralEvent.Seq);
                insert.Parameters.AddWithValue("@sourceChannel", batch.Channel);

                if (insert.ExecuteNonQuery() == 1)
                {
                    storedSequences.Add(behavioralEvent.Seq);
                    nextLocalSequence++;
                }
            }

            using (var upsert = _conn.CreateCommand())
            {
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO behavioral_delivery_streams
                        (stream_id, channel, last_sequence, dropped_total,
                         sequence_gap_total, accepted_batch_count, duplicate_batch_count,
                         rejected_event_count, last_batch_id, last_session_id, last_received_at)
                    VALUES
                        (@stream, @channel, @lastSequence, @droppedTotal,
                         @sequenceGap, 1, 0,
                         @rejected, @batch, @session, @received)
                    ON CONFLICT(stream_id) DO UPDATE SET
                        last_sequence = excluded.last_sequence,
                        dropped_total = MAX(behavioral_delivery_streams.dropped_total, excluded.dropped_total),
                        sequence_gap_total = behavioral_delivery_streams.sequence_gap_total + excluded.sequence_gap_total,
                        accepted_batch_count = behavioral_delivery_streams.accepted_batch_count + 1,
                        rejected_event_count = behavioral_delivery_streams.rejected_event_count + excluded.rejected_event_count,
                        last_batch_id = excluded.last_batch_id,
                        last_session_id = excluded.last_session_id,
                        last_received_at = excluded.last_received_at
                    """;
                upsert.Parameters.AddWithValue("@stream", batch.StreamId);
                upsert.Parameters.AddWithValue("@channel", batch.Channel);
                upsert.Parameters.AddWithValue("@lastSequence", batch.LastSequence);
                upsert.Parameters.AddWithValue("@droppedTotal", batch.DroppedTotal);
                upsert.Parameters.AddWithValue("@sequenceGap", sequenceGap);
                upsert.Parameters.AddWithValue("@rejected", rejectedEvents);
                upsert.Parameters.AddWithValue("@batch", batch.BatchId);
                upsert.Parameters.AddWithValue("@session", sessionId);
                upsert.Parameters.AddWithValue("@received", DateTimeOffset.UtcNow.ToString("o"));
                upsert.ExecuteNonQuery();
            }

            UpsertBehavioralSessionMetrics(
                batch.StreamId,
                sessionId,
                batch.Channel,
                droppedDelta,
                sequenceGap,
                rejectedEvents,
                batch.ContractVersion == BehavioralEventBatch.CurrentContractVersion,
                transaction);

            transaction.Commit();
            return new BehavioralDeliveryCommit(
                Duplicate: false,
                AcceptedThroughSequence: batch.LastSequence,
                EventsStored: storedSequences.Count,
                EventsRejected: rejectedEvents,
                SequenceGap: sequenceGap,
                DroppedDelta: droppedDelta,
                StoredSourceSequences: storedSequences);
        }
    }

    public void MarkLegacyBehavioralDeliverySeen()
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE behavioral_delivery_health
                SET legacy_unverified_seen = 1,
                    last_legacy_received_at = @received
                WHERE singleton_id = 1
                """;
            command.Parameters.AddWithValue("@received", DateTimeOffset.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    public void RecordObservationSpoolStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)
            || status.Length > 96
            || status.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw new ArgumentException("Invalid observation spool status.", nameof(status));
        }

        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                UPDATE behavioral_delivery_health
                SET observation_spool_status = @status,
                    last_observation_spool_status_at = @received
                WHERE singleton_id = 1
                """;
            command.Parameters.AddWithValue("@status", status);
            command.Parameters.AddWithValue("@received", DateTimeOffset.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }
    }

    public BehavioralDeliveryHealth GetBehavioralDeliveryHealth(string? sessionId = null)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = sessionId is null
                ? """
                SELECT
                    COUNT(DISTINCT stream_id),
                    COUNT(DISTINCT CASE WHEN channel = 'pms' THEN stream_id END),
                    COUNT(DISTINCT CASE WHEN channel = 'system' THEN stream_id END),
                    COALESCE(SUM(dropped_event_count), 0),
                    COALESCE(SUM(sequence_gap_count), 0),
                    COALESCE(SUM(accepted_batch_count), 0),
                    COALESCE(SUM(verified_batch_count), 0),
                    COALESCE(SUM(duplicate_batch_count), 0),
                    COALESCE(SUM(rejected_event_count), 0),
                    MAX(last_received_at),
                    MAX(CASE WHEN channel = 'pms' THEN last_received_at END),
                    MAX(CASE WHEN channel = 'system' THEN last_received_at END),
                    MAX(last_verified_received_at),
                    MAX(CASE WHEN channel = 'pms' THEN last_verified_received_at END),
                    MAX(CASE WHEN channel = 'system' THEN last_verified_received_at END),
                    (SELECT legacy_unverified_seen
                     FROM behavioral_delivery_health
                     WHERE singleton_id = 1),
                    (SELECT last_legacy_received_at
                     FROM behavioral_delivery_health
                     WHERE singleton_id = 1),
                    (SELECT observation_spool_status
                     FROM behavioral_delivery_health
                     WHERE singleton_id = 1),
                    (SELECT last_observation_spool_status_at
                     FROM behavioral_delivery_health
                     WHERE singleton_id = 1)
                FROM behavioral_delivery_session_metrics
                """
                : """
                SELECT
                    COUNT(DISTINCT stream_id),
                    COUNT(DISTINCT CASE WHEN channel = 'pms' THEN stream_id END),
                    COUNT(DISTINCT CASE WHEN channel = 'system' THEN stream_id END),
                    COALESCE(SUM(dropped_event_count), 0),
                    COALESCE(SUM(sequence_gap_count), 0),
                    COALESCE(SUM(accepted_batch_count), 0),
                    COALESCE(SUM(verified_batch_count), 0),
                    COALESCE(SUM(duplicate_batch_count), 0),
                    COALESCE(SUM(rejected_event_count), 0),
                    MAX(last_received_at),
                    MAX(CASE WHEN channel = 'pms' THEN last_received_at END),
                    MAX(CASE WHEN channel = 'system' THEN last_received_at END),
                    MAX(last_verified_received_at),
                    MAX(CASE WHEN channel = 'pms' THEN last_verified_received_at END),
                    MAX(CASE WHEN channel = 'system' THEN last_verified_received_at END),
                    (SELECT legacy_unverified_seen
                     FROM behavioral_delivery_health
                     WHERE singleton_id = 1),
                    (SELECT last_legacy_received_at
                     FROM behavioral_delivery_health
                     WHERE singleton_id = 1),
                    (SELECT observation_spool_status
                     FROM behavioral_delivery_health
                     WHERE singleton_id = 1),
                    (SELECT last_observation_spool_status_at
                     FROM behavioral_delivery_health
                     WHERE singleton_id = 1)
                FROM behavioral_delivery_session_metrics
                WHERE session_id = @session
                """;
            if (sessionId is not null)
                command.Parameters.AddWithValue("@session", sessionId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return new BehavioralDeliveryHealth(
                    0, 0, 0, 0, 0, 0, 0, 0, 0,
                    null, null, null, null, null, null, false, null, "not_reported", null);

            var lastBatch = reader.IsDBNull(9)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(9));
            var lastPmsBatch = reader.IsDBNull(10)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(10));
            var lastSystemBatch = reader.IsDBNull(11)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(11));
            var lastVerifiedBatch = reader.IsDBNull(12)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(12));
            var lastVerifiedPmsBatch = reader.IsDBNull(13)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(13));
            var lastVerifiedSystemBatch = reader.IsDBNull(14)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(14));
            var lastLegacy = reader.IsDBNull(16)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(16));
            var spoolStatus = reader.IsDBNull(17) ? "not_reported" : reader.GetString(17);
            var lastSpoolStatus = reader.IsDBNull(18)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(18));
            return new BehavioralDeliveryHealth(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                lastBatch,
                lastPmsBatch,
                lastSystemBatch,
                lastVerifiedBatch,
                lastVerifiedPmsBatch,
                lastVerifiedSystemBatch,
                !reader.IsDBNull(15) && reader.GetInt64(15) != 0,
                lastLegacy,
                spoolStatus,
                lastSpoolStatus);
        }
    }

    public ObserverStatusSnapshot? GetLatestObserverStatus(string observer)
    {
        lock (_connLock)
        {
            using var command = _conn.CreateCommand();
            command.CommandText = """
                SELECT element_id, received_at
                FROM behavioral_events
                WHERE event_type = 'observerstatus'
                  AND event_subtype = @observer
                  AND element_id IS NOT NULL
                ORDER BY id DESC
                LIMIT 1
                """;
            command.Parameters.AddWithValue("@observer", observer);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            return new ObserverStatusSnapshot(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1)));
        }
    }

    private (string Channel, long LastSequence, long DroppedTotal, string LastSessionId)? ReadBehavioralDeliveryState(
        string streamId,
        SqliteTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT channel, last_sequence, dropped_total, last_session_id
            FROM behavioral_delivery_streams
            WHERE stream_id = @stream
            """;
        command.Parameters.AddWithValue("@stream", streamId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3))
            : null;
    }

    private void UpsertBehavioralSessionMetrics(
        string streamId,
        string sessionId,
        string channel,
        long droppedDelta,
        long sequenceGap,
        int rejectedEvents,
        bool verified,
        SqliteTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO behavioral_delivery_session_metrics
                (stream_id, session_id, channel, dropped_event_count,
                 sequence_gap_count, accepted_batch_count, verified_batch_count,
                 duplicate_batch_count, rejected_event_count, last_received_at,
                 last_verified_received_at)
            VALUES
                (@stream, @session, @channel, @dropped,
                 @gap, 1, @verified, 0, @rejected, @received,
                 CASE WHEN @verified = 1 THEN @received ELSE NULL END)
            ON CONFLICT(stream_id, session_id) DO UPDATE SET
                dropped_event_count = behavioral_delivery_session_metrics.dropped_event_count + excluded.dropped_event_count,
                sequence_gap_count = behavioral_delivery_session_metrics.sequence_gap_count + excluded.sequence_gap_count,
                accepted_batch_count = behavioral_delivery_session_metrics.accepted_batch_count + 1,
                verified_batch_count = behavioral_delivery_session_metrics.verified_batch_count + excluded.verified_batch_count,
                rejected_event_count = behavioral_delivery_session_metrics.rejected_event_count + excluded.rejected_event_count,
                last_received_at = excluded.last_received_at,
                last_verified_received_at = COALESCE(
                    excluded.last_verified_received_at,
                    behavioral_delivery_session_metrics.last_verified_received_at)
            """;
        command.Parameters.AddWithValue("@stream", streamId);
        command.Parameters.AddWithValue("@session", sessionId);
        command.Parameters.AddWithValue("@channel", channel);
        command.Parameters.AddWithValue("@dropped", droppedDelta);
        command.Parameters.AddWithValue("@gap", sequenceGap);
        command.Parameters.AddWithValue("@rejected", rejectedEvents);
        command.Parameters.AddWithValue("@verified", verified ? 1 : 0);
        command.Parameters.AddWithValue("@received", DateTimeOffset.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }

    private void IncrementBehavioralDuplicateMetric(
        string streamId,
        string sessionId,
        string channel,
        SqliteTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO behavioral_delivery_session_metrics
                (stream_id, session_id, channel, dropped_event_count,
                 sequence_gap_count, accepted_batch_count, duplicate_batch_count,
                 rejected_event_count, last_received_at)
            VALUES
                (@stream, @session, @channel, 0, 0, 0, 1, 0, @received)
            ON CONFLICT(stream_id, session_id) DO UPDATE SET
                duplicate_batch_count = behavioral_delivery_session_metrics.duplicate_batch_count + 1,
                last_received_at = excluded.last_received_at
            """;
        command.Parameters.AddWithValue("@stream", streamId);
        command.Parameters.AddWithValue("@session", sessionId);
        command.Parameters.AddWithValue("@channel", channel);
        command.Parameters.AddWithValue("@received", DateTimeOffset.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }

    private int ReadNextBehavioralSequence(string sessionId, SqliteTransaction transaction)
    {
        using var command = _conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(sequence_num), 0) + 1
            FROM behavioral_events
            WHERE session_id = @session
            """;
        command.Parameters.AddWithValue("@session", sessionId);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
