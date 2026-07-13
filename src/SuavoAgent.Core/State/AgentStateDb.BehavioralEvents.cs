using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    // ── Behavioral Events ──

    public void InsertBehavioralEvent(string sessionId, int sequenceNum, string eventType,
        string? eventSubtype, string? treeHash, string? elementId, string? controlType,
        string? className, string? nameHash, string? boundingRect,
        string? keystrokeCategory, string? timingBucket, int? keystrokeCount, int occurrenceCount,
        string helperTimestamp,
        string sourceChannel = BehavioralEventChannels.Pms)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO behavioral_events
                (session_id, sequence_num, event_type, event_subtype, tree_hash,
                 element_id, element_control_type, element_class_name, element_name_hash,
                 element_bounding_rect, keystroke_category, keystroke_timing_bucket,
                 keystroke_sequence_count, occurrence_count, helper_timestamp, received_at,
                 source_channel)
            VALUES
                (@sid, @seq, @type, @subtype, @treeHash,
                 @elemId, @ctrlType, @className, @nameHash,
                 @boundRect, @ksCat, @ksBucket,
                 @ksCount, @occCount, @helperTs, @now,
                 @sourceChannel)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@seq", sequenceNum);
        cmd.Parameters.AddWithValue("@type", eventType);
        cmd.Parameters.AddWithValue("@subtype", (object?)eventSubtype ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@treeHash", (object?)treeHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@elemId", (object?)elementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ctrlType", (object?)controlType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@className", (object?)className ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nameHash", (object?)nameHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@boundRect", (object?)boundingRect ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ksCat", (object?)keystrokeCategory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ksBucket", (object?)timingBucket ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ksCount", (object?)keystrokeCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@occCount", occurrenceCount);
        cmd.Parameters.AddWithValue("@helperTs", helperTimestamp);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@sourceChannel", sourceChannel);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(long Id, int SequenceNum, string EventType, string? EventSubtype,
        string? TreeHash, string? ElementId, string? ControlType, int OccurrenceCount, string HelperTimestamp)>
        GetBehavioralEvents(
            string sessionId,
            string? eventType = null,
            int limit = 1000,
            string? sourceChannel = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = eventType is null
            ? """
              SELECT id, sequence_num, event_type, event_subtype, tree_hash, element_id,
                     element_control_type, occurrence_count, helper_timestamp
              FROM behavioral_events
              WHERE session_id = @sid
                AND (@sourceChannel IS NULL OR COALESCE(source_channel, 'pms') = @sourceChannel)
              ORDER BY sequence_num
              LIMIT @limit
              """
            : """
              SELECT id, sequence_num, event_type, event_subtype, tree_hash, element_id,
                     element_control_type, occurrence_count, helper_timestamp
              FROM behavioral_events
              WHERE session_id = @sid AND event_type = @type
                AND (@sourceChannel IS NULL OR COALESCE(source_channel, 'pms') = @sourceChannel)
              ORDER BY sequence_num
              LIMIT @limit
              """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@sourceChannel", (object?)sourceChannel ?? DBNull.Value);
        if (eventType is not null)
            cmd.Parameters.AddWithValue("@type", eventType);

        var results = new List<(long, int, string, string?, string?, string?, string?, int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7),
                reader.GetString(8)));
        }
        return results;
    }

    // ── DMV Query Observations ──

    public void UpsertDmvQueryObservation(string sessionId, string queryShapeHash, string queryShape,
        string tablesReferenced, bool isWrite, int executionCount, string lastExecutionTime, int clockOffsetMs)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO dmv_query_observations
                (session_id, query_shape_hash, query_shape, tables_referenced, is_write,
                 execution_count, last_execution_time, clock_offset_ms, first_seen, last_seen)
            VALUES
                (@sid, @hash, @shape, @tables, @isWrite,
                 @execCount, @lastExec, @clockOffset, @now, @now)
            ON CONFLICT(session_id, query_shape_hash) DO UPDATE SET
                execution_count = execution_count + @execCount,
                last_execution_time = @lastExec,
                clock_offset_ms = @clockOffset,
                last_seen = @now
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@hash", queryShapeHash);
        cmd.Parameters.AddWithValue("@shape", queryShape);
        cmd.Parameters.AddWithValue("@tables", tablesReferenced);
        cmd.Parameters.AddWithValue("@isWrite", isWrite ? 1 : 0);
        cmd.Parameters.AddWithValue("@execCount", executionCount);
        cmd.Parameters.AddWithValue("@lastExec", lastExecutionTime);
        cmd.Parameters.AddWithValue("@clockOffset", clockOffsetMs);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string QueryShapeHash, string QueryShape, string TablesReferenced,
        bool IsWrite, int ExecutionCount, string LastExecutionTime)>
        GetDmvQueryObservations(string sessionId, int limit = 500)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT query_shape_hash, query_shape, tables_referenced, is_write, execution_count, last_execution_time
            FROM dmv_query_observations
            WHERE session_id = @sid
            ORDER BY last_execution_time DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<(string, string, string, bool, int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) == 1,
                reader.GetInt32(4),
                reader.GetString(5)));
        }
        return results;
    }

    public IReadOnlyList<(string QueryShapeHash, string QueryShape, string TablesReferenced,
        bool IsWrite, int ExecutionCount, string LastExecutionTime)>
        GetRecentDmvQueries(string sessionId, string sinceTimestamp)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT query_shape_hash, query_shape, tables_referenced, is_write, execution_count, last_execution_time
            FROM dmv_query_observations
            WHERE session_id = @sid AND last_execution_time > @since
            ORDER BY last_execution_time DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@since", sinceTimestamp);

        var results = new List<(string, string, string, bool, int, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3) == 1,
                reader.GetInt32(4),
                reader.GetString(5)));
        }
        return results;
    }

    // ── Correlated Actions ──

    public void UpsertCorrelatedAction(string sessionId, string correlationKey, string treeHash,
        string elementId, string? controlType, string? queryShapeHash, bool isWrite, string? tablesReferenced,
        bool seededShape = false)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO correlated_actions
                (session_id, correlation_key, tree_hash, element_id, element_control_type,
                 query_shape_hash, query_is_write, tables_referenced, occurrence_count,
                 confidence, first_seen, last_seen)
            VALUES
                (@sid, @key, @treeHash, @elemId, @ctrlType,
                 @qHash, @isWrite, @tables, 1,
                 0.3, @now, @now)
            ON CONFLICT(session_id, correlation_key) DO UPDATE SET
                occurrence_count = occurrence_count + 1,
                confidence = CASE
                    WHEN occurrence_count + 1 >= 10 THEN 0.9
                    WHEN occurrence_count + 1 >= @threshold THEN 0.6
                    ELSE 0.3
                END,
                last_seen = @now
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.Parameters.AddWithValue("@treeHash", treeHash);
        cmd.Parameters.AddWithValue("@elemId", elementId);
        cmd.Parameters.AddWithValue("@ctrlType", (object?)controlType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@qHash", (object?)queryShapeHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isWrite", isWrite ? 1 : 0);
        cmd.Parameters.AddWithValue("@tables", (object?)tablesReferenced ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@threshold", seededShape ? 2 : 3);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string CorrelationKey, string TreeHash, string ElementId,
        string? ControlType, string? QueryShapeHash, bool IsWrite, string? TablesReferenced,
        int OccurrenceCount, double Confidence)>
        GetCorrelatedActions(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, tree_hash, element_id, element_control_type,
                   query_shape_hash, query_is_write, tables_referenced,
                   occurrence_count, confidence
            FROM correlated_actions
            WHERE session_id = @sid
            ORDER BY occurrence_count DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);

        var results = new List<(string, string, string, string?, string?, bool, string?, int, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt32(7),
                reader.GetDouble(8)));
        }
        return results;
    }

    public IReadOnlyList<(string CorrelationKey, string TreeHash, string ElementId,
        string? ControlType, string? QueryShape, string? TablesReferenced,
        int OccurrenceCount, double Confidence)>
        GetWritebackCandidates(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT ca.correlation_key, ca.tree_hash, ca.element_id, ca.element_control_type,
                   dqo.query_shape, ca.tables_referenced,
                   ca.occurrence_count, ca.confidence
            FROM correlated_actions ca
            LEFT JOIN dmv_query_observations dqo
                ON ca.session_id = dqo.session_id AND ca.query_shape_hash = dqo.query_shape_hash
            WHERE ca.session_id = @sid AND ca.query_is_write = 1
            ORDER BY ca.occurrence_count DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);

        var results = new List<(string, string, string, string?, string?, string?, int, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetDouble(7)));
        }
        return results;
    }

    // ── Learned Routines ──

    public void UpsertLearnedRoutine(string sessionId, string routineHash, string pathJson,
        int pathLength, int frequency, double confidence, string? startElementId, string? endElementId,
        string? correlatedWriteQueries, bool hasWritebackCandidate)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO learned_routines
                (session_id, routine_hash, path_json, path_length, frequency, confidence,
                 start_element_id, end_element_id, correlated_write_queries,
                 has_writeback_candidate, discovered_at, last_observed)
            VALUES
                (@sid, @hash, @path, @len, @freq, @conf,
                 @startElem, @endElem, @writeQueries,
                 @hasWriteback, @now, @now)
            ON CONFLICT(session_id, routine_hash) DO UPDATE SET
                frequency = @freq,
                confidence = @conf,
                correlated_write_queries = @writeQueries,
                has_writeback_candidate = @hasWriteback,
                last_observed = @now
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@hash", routineHash);
        cmd.Parameters.AddWithValue("@path", pathJson);
        cmd.Parameters.AddWithValue("@len", pathLength);
        cmd.Parameters.AddWithValue("@freq", frequency);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@startElem", (object?)startElementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@endElem", (object?)endElementId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@writeQueries", (object?)correlatedWriteQueries ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hasWriteback", hasWritebackCandidate ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string RoutineHash, string PathJson, int PathLength, int Frequency,
        double Confidence, string? StartElementId, string? EndElementId,
        string? CorrelatedWriteQueries, bool HasWritebackCandidate)>
        GetLearnedRoutines(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT routine_hash, path_json, path_length, frequency, confidence,
                   start_element_id, end_element_id, correlated_write_queries, has_writeback_candidate
            FROM learned_routines
            WHERE session_id = @sid
            ORDER BY frequency DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);

        var results = new List<(string, string, int, int, double, string?, string?, string?, bool)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8) == 1));
        }
        return results;
    }

}
