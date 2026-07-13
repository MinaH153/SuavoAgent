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
    // ── Behavioral Events: structural lookups (v3.12 extractor support) ──

    /// <summary>
    /// Returns the most-recently-observed {ControlType, ClassName} pair for a
    /// given (treeHash, elementId). Null when the pair has never been seen in
    /// this session. Extractor uses this to build a
    /// <see cref="SuavoAgent.Contracts.Behavioral.ElementSignature"/> per step.
    /// </summary>
    public (string? ControlType, string? ClassName)? GetElementStructure(
        string sessionId, string treeHash, string elementId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT element_control_type, element_class_name
            FROM behavioral_events
            WHERE session_id = @sid AND tree_hash = @tree AND element_id = @elem
              AND COALESCE(source_channel, 'pms') = 'pms'
            ORDER BY id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tree", treeHash);
        cmd.Parameters.AddWithValue("@elem", elementId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>
    /// Distinct {ControlType, AutomationId (element_id), ClassName} triples seen
    /// on a particular tree_hash — the building block for a screen's
    /// ExpectedVisible list. Only emits rows where element_id looks like an
    /// AutomationId (no colon fallback form); anonymous/fallback elements
    /// cannot cross installations and are excluded from templates.
    /// </summary>
    public IReadOnlyList<(string ControlType, string ElementId, string? ClassName, int OccurrenceCount)>
        GetDistinctElementsOnTree(string sessionId, string treeHash)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT element_control_type, element_id, element_class_name,
                   SUM(occurrence_count) AS total_occ
            FROM behavioral_events
            WHERE session_id = @sid
              AND tree_hash = @tree
              AND element_id IS NOT NULL
              AND element_control_type IS NOT NULL
              AND instr(element_id, ':') = 0
              AND COALESCE(source_channel, 'pms') = 'pms'
            GROUP BY element_control_type, element_id, element_class_name
            ORDER BY total_occ DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tree", treeHash);

        var rows = new List<(string, string, string?, int)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3)));
        }
        return rows;
    }

    // ── Behavioral Telemetry Counts ──

    public int GetBehavioralEventCount(string sessionId, string? eventType = null)
    {
        using var cmd = _conn.CreateCommand();
        if (eventType is null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM behavioral_events WHERE session_id = @sid";
            cmd.Parameters.AddWithValue("@sid", sessionId);
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM behavioral_events WHERE session_id = @sid AND event_type = @type";
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@type", eventType);
        }
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetUniqueScreenCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT tree_hash) FROM behavioral_events
            WHERE session_id = @sid AND tree_hash IS NOT NULL
              AND COALESCE(source_channel, 'pms') = 'pms'
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<string> GetDistinctTreeHashes(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT tree_hash FROM behavioral_events
            WHERE session_id = @sid AND tree_hash IS NOT NULL
              AND COALESCE(source_channel, 'pms') = 'pms'
            ORDER BY tree_hash
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        using var reader = cmd.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results;
    }

    public string? GetFirstBehavioralEventTimestamp(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT MIN(received_at) FROM behavioral_events
            WHERE session_id = @sid
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var result = cmd.ExecuteScalar();
        return result is DBNull || result is null ? null : result.ToString();
    }

    public int GetDmvWriteShapeCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM dmv_query_observations WHERE session_id = @sid AND is_write = 1";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetCorrelatedActionCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM correlated_actions WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetWritebackCandidateCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM correlated_actions WHERE session_id = @sid AND query_is_write = 1";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetLearnedRoutineCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM learned_routines WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetWorkflowTemplateCount(string? skillId = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = skillId is null
            ? "SELECT COUNT(*) FROM workflow_templates WHERE retired_at IS NULL"
            : "SELECT COUNT(*) FROM workflow_templates WHERE retired_at IS NULL AND skill_id = @skill";
        if (skillId is not null) cmd.Parameters.AddWithValue("@skill", skillId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetRoutinesWithWritebackCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM learned_routines WHERE session_id = @sid AND has_writeback_candidate = 1";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ── Behavioral Event Pruning ──

    /// <summary>
    /// Deletes behavioral_events older than <paramref name="olderThanDays"/> days
    /// where the event's tree_hash appears in a stable learned routine (frequency >= 5).
    /// Returns the number of rows deleted.
    /// </summary>
    public int PruneBehavioralEvents(string sessionId, int olderThanDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays).ToString("o");
        // D9: Use transaction + single-command changes() to avoid race with other threads
        using var txn = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = txn;
        cmd.CommandText = """
            DELETE FROM behavioral_events
            WHERE session_id = @sid
              AND received_at < @cutoff
              AND tree_hash IN (
                  SELECT DISTINCT je.value
                  FROM learned_routines lr, json_each(lr.path_json) je
                  WHERE lr.session_id = @sid AND lr.frequency >= 5
              );
            SELECT changes();
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var result = cmd.ExecuteScalar();
        txn.Commit();
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Prunes behavioral events older than the specified retention period.
    /// Prevents unbounded disk growth (~2 MB/day = 730 MB/year without pruning).
    /// </summary>
    public int PruneBehavioralEventsByAge(TimeSpan retention)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM behavioral_events WHERE received_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.Subtract(retention).ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Prunes app_sessions older than the specified retention period.
    /// </summary>
    public int PruneAppSessionsByAge(TimeSpan retention)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM app_sessions WHERE start_ts < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.Subtract(retention).ToString("o"));
        return cmd.ExecuteNonQuery();
    }

    // ── Feedback Events CRUD ──

    public int InsertFeedbackEvent(FeedbackEvent evt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO feedback_events
                (session_id, event_type, source, source_id, target_type, target_id,
                 payload_json, directive_type, directive_json, applied_at, applied_by,
                 causal_chain_json, created_at)
            VALUES
                (@sid, @eventType, @source, @sourceId, @targetType, @targetId,
                 @payload, @directive, @directiveJson, @appliedAt, @appliedBy,
                 @causalChain, @createdAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@sid", evt.SessionId);
        cmd.Parameters.AddWithValue("@eventType", evt.EventType);
        cmd.Parameters.AddWithValue("@source", evt.Source);
        cmd.Parameters.AddWithValue("@sourceId", (object?)evt.SourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@targetType", evt.TargetType);
        cmd.Parameters.AddWithValue("@targetId", evt.TargetId);
        cmd.Parameters.AddWithValue("@payload", (object?)evt.PayloadJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@directive", evt.DirectiveType.ToString());
        cmd.Parameters.AddWithValue("@directiveJson", (object?)evt.DirectiveJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@appliedAt", (object?)evt.AppliedAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@appliedBy", (object?)evt.AppliedBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@causalChain", (object?)evt.CausalChainJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", evt.CreatedAt);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public FeedbackEvent? GetFeedbackEvent(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                   payload_json, directive_type, directive_json, applied_at, applied_by,
                   causal_chain_json, created_at
            FROM feedback_events WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFeedbackEvent(r) : null;
    }

    public IReadOnlyList<FeedbackEvent> GetPendingFeedbackEvents(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                   payload_json, directive_type, directive_json, applied_at, applied_by,
                   causal_chain_json, created_at
            FROM feedback_events
            WHERE session_id = @sid AND applied_at IS NULL
            ORDER BY id ASC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<FeedbackEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(ReadFeedbackEvent(r));
        return results;
    }

    public void MarkFeedbackEventApplied(int id, string appliedBy)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE feedback_events SET applied_at = @now, applied_by = @by WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@by", appliedBy);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public bool HasDecayEventToday(string sessionId, string targetId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM feedback_events
            WHERE session_id = @sid AND target_id = @tid AND source = 'decay'
              AND created_at >= @today
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tid", targetId);
        cmd.Parameters.AddWithValue("@today", DateTime.UtcNow.Date.ToString("o"));
        return cmd.ExecuteScalar() is not null;
    }

    public void UpdateCorrelationConfidence(string sessionId, string correlationKey, double newConfidence)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE correlated_actions SET confidence = @conf
            WHERE session_id = @sid AND correlation_key = @key
            """;
        cmd.Parameters.AddWithValue("@conf", newConfidence);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCorrelationFlags(string sessionId, string correlationKey,
        bool? operatorApproved = null, bool? operatorRejected = null,
        bool? promotionSuspended = null, int? consecutiveFailures = null,
        bool? stale = null, string? staleSince = null)
    {
        var setClauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        if (operatorApproved.HasValue)
        {
            setClauses.Add("operator_approved = @opApproved");
            parameters.Add(("@opApproved", operatorApproved.Value ? 1 : 0));
        }
        if (operatorRejected.HasValue)
        {
            setClauses.Add("operator_rejected = @opRejected");
            parameters.Add(("@opRejected", operatorRejected.Value ? 1 : 0));
        }
        if (promotionSuspended.HasValue)
        {
            setClauses.Add("promotion_suspended = @promoSuspended");
            parameters.Add(("@promoSuspended", promotionSuspended.Value ? 1 : 0));
        }
        if (consecutiveFailures.HasValue)
        {
            setClauses.Add("consecutive_failures = @consecFail");
            parameters.Add(("@consecFail", consecutiveFailures.Value));
        }
        if (stale.HasValue)
        {
            setClauses.Add("stale = @stale");
            parameters.Add(("@stale", stale.Value ? 1 : 0));
        }
        if (staleSince is not null)
        {
            setClauses.Add("stale_since = @staleSince");
            parameters.Add(("@staleSince", staleSince));
        }

        if (setClauses.Count == 0) return;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"UPDATE correlated_actions SET {string.Join(", ", setClauses)} WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public (bool OperatorApproved, bool OperatorRejected, bool PromotionSuspended,
        int ConsecutiveFailures, bool Stale, string? StaleSince)?
        GetCorrelatedActionExtended(string sessionId, string correlationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT operator_approved, operator_rejected, promotion_suspended,
                   consecutive_failures, stale, stale_since
            FROM correlated_actions
            WHERE session_id = @sid AND correlation_key = @key
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (
            reader.GetInt32(0) == 1,
            reader.GetInt32(1) == 1,
            reader.GetInt32(2) == 1,
            reader.GetInt32(3),
            reader.GetInt32(4) == 1,
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public int GetFeedbackEventCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM feedback_events WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public int GetFeedbackEventCountByApplier(string sessionId, string appliedBy)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM feedback_events WHERE session_id = @sid AND applied_by = @by";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@by", appliedBy);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<string> GetSuspendedPromotions(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key FROM correlated_actions
            WHERE session_id = @sid AND promotion_suspended = 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

    public IReadOnlyList<(string CorrelationKey, string StaleSince)> GetExpiredStaleCorrelations(string sessionId, int ttlDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-ttlDays).ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, stale_since FROM correlated_actions
            WHERE session_id = @sid AND stale = 1 AND stale_since < @cutoff
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var results = new List<(string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public bool HasReplacementCorrelation(string sessionId, string treeHash, string elementId, string excludeCorrelationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT 1 FROM correlated_actions
            WHERE session_id = @sid AND tree_hash = @th AND element_id = @eid
              AND stale = 0 AND correlation_key != @exclude
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@th", treeHash);
        cmd.Parameters.AddWithValue("@eid", elementId);
        cmd.Parameters.AddWithValue("@exclude", excludeCorrelationKey);
        return cmd.ExecuteScalar() is not null;
    }

    public IReadOnlyList<(string CorrelationKey, double Confidence, string LastSeen)> GetIdleCorrelations(string sessionId, int idleDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-idleDays).ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT correlation_key, confidence, last_seen FROM correlated_actions
            WHERE session_id = @sid AND last_seen < @cutoff AND confidence > 0.5
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var results = new List<(string, double, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add((reader.GetString(0), reader.GetDouble(1), reader.GetString(2)));
        return results;
    }

    public void DeleteCorrelation(string sessionId, string correlationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM correlated_actions WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.ExecuteNonQuery();
    }

    public void UpsertWindowOverride(string sessionId, string treeHash, string elementId, double windowSeconds, int sampleCount)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO correlation_window_overrides
                (session_id, tree_hash, element_id, window_seconds, sample_count, computed_at)
            VALUES (@sid, @th, @eid, @window, @samples, @now)
            ON CONFLICT(session_id, tree_hash, element_id) DO UPDATE SET
                window_seconds = @window,
                sample_count = @samples,
                computed_at = @now
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@th", treeHash);
        cmd.Parameters.AddWithValue("@eid", elementId);
        cmd.Parameters.AddWithValue("@window", windowSeconds);
        cmd.Parameters.AddWithValue("@samples", sampleCount);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public double? GetWindowOverride(string sessionId, string treeHash, string elementId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT window_seconds FROM correlation_window_overrides
            WHERE session_id = @sid AND tree_hash = @th AND element_id = @eid
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@th", treeHash);
        cmd.Parameters.AddWithValue("@eid", elementId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : Convert.ToDouble(result);
    }

    public int GetWindowOverrideCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM correlation_window_overrides WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public IReadOnlyList<FeedbackEvent> GetFeedbackEventsForTarget(string sessionId, string targetId, string? source = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = source is null
            ? """
              SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                     payload_json, directive_type, directive_json, applied_at, applied_by,
                     causal_chain_json, created_at
              FROM feedback_events
              WHERE session_id = @sid AND target_id = @tid
              ORDER BY id ASC
              """
            : """
              SELECT id, session_id, event_type, source, source_id, target_type, target_id,
                     payload_json, directive_type, directive_json, applied_at, applied_by,
                     causal_chain_json, created_at
              FROM feedback_events
              WHERE session_id = @sid AND target_id = @tid AND source = @src
              ORDER BY id ASC
              """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tid", targetId);
        if (source is not null)
            cmd.Parameters.AddWithValue("@src", source);
        var results = new List<FeedbackEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) results.Add(ReadFeedbackEvent(r));
        return results;
    }

    public void RemoveWritebackFlagForCorrelation(string sessionId, string correlationKey)
    {
        // Look up the query_shape_hash for this correlation
        using var lookupCmd = _conn.CreateCommand();
        lookupCmd.CommandText = """
            SELECT query_shape_hash FROM correlated_actions
            WHERE session_id = @sid AND correlation_key = @key
            """;
        lookupCmd.Parameters.AddWithValue("@sid", sessionId);
        lookupCmd.Parameters.AddWithValue("@key", correlationKey);
        var hash = lookupCmd.ExecuteScalar();
        if (hash is null or DBNull) return;

        // D12: Use json_each for exact match instead of LIKE '%hash%' (substring collision risk)
        // correlated_write_queries is a JSON array of shape hashes
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learned_routines SET has_writeback_candidate = 0
            WHERE session_id = @sid
              AND EXISTS (
                  SELECT 1 FROM json_each(correlated_write_queries)
                  WHERE value = @hash
              )
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@hash", (string)hash);
        cmd.ExecuteNonQuery();
    }

    private static FeedbackEvent ReadFeedbackEvent(SqliteDataReader r)
    {
        return new FeedbackEvent(
            SessionId: r.GetString(1),
            EventType: r.GetString(2),
            Source: r.GetString(3),
            SourceId: r.IsDBNull(4) ? null : r.GetString(4),
            TargetType: r.GetString(5),
            TargetId: r.GetString(6),
            PayloadJson: r.IsDBNull(7) ? null : r.GetString(7),
            DirectiveType: Enum.Parse<DirectiveType>(r.GetString(8)),
            DirectiveJson: r.IsDBNull(9) ? null : r.GetString(9),
            CausalChainJson: r.IsDBNull(12) ? null : r.GetString(12))
        {
            Id = r.GetInt32(0),
            AppliedAt = r.IsDBNull(10) ? null : r.GetString(10),
            AppliedBy = r.IsDBNull(11) ? null : r.GetString(11),
            CreatedAt = r.GetString(13)
        };
    }

    public IReadOnlyList<string> GetRecentWritebackTargets(string sessionId, int withinDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-withinDays).ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT target_id FROM feedback_events
            WHERE session_id = @sid AND source = 'writeback' AND created_at >= @cutoff
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        var results = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(reader.GetString(0));
        return results;
    }

}
