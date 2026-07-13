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
    // --- Spec D: Seed state methods ---

    public record CorrelationSource(string Source, string? SeedDigest, string? SeededAt);

    public CorrelationSource GetCorrelatedActionSource(string sessionId, string correlationKey)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT source, seed_digest, seeded_at FROM correlated_actions WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new("local", null, null);
        return new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    public void SetCorrelatedActionSource(string sessionId, string correlationKey, string source, string? seedDigest, string? seededAt)
    {
        if (source == "seed" && seedDigest is null)
            throw new ArgumentException("seed_digest must not be null when source is 'seed'");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE correlated_actions SET source = @src, seed_digest = @dig, seeded_at = @at WHERE session_id = @sid AND correlation_key = @key";
        cmd.Parameters.AddWithValue("@src", source);
        cmd.Parameters.AddWithValue("@dig", (object?)seedDigest ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", (object?)seededAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@key", correlationKey);
        cmd.ExecuteNonQuery();
    }

    public record AppliedSeed(string SeedDigest, string Phase, string AppliedAt, int CorrelationsApplied, int CorrelationsSkipped);

    public void InsertAppliedSeed(
        string seedDigest,
        string phase,
        string appliedAt,
        int correlationsApplied,
        int correlationsSkipped,
        string? sessionId = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO applied_seeds
                (seed_digest, phase, applied_at, correlations_applied, correlations_skipped, session_id)
            VALUES (@d, @p, @a, @ca, @cs, @sid)
            """;
        cmd.Parameters.AddWithValue("@d", seedDigest);
        cmd.Parameters.AddWithValue("@p", phase);
        cmd.Parameters.AddWithValue("@a", appliedAt);
        cmd.Parameters.AddWithValue("@ca", correlationsApplied);
        cmd.Parameters.AddWithValue("@cs", correlationsSkipped);
        cmd.Parameters.AddWithValue("@sid", (object?)sessionId ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public AppliedSeed? GetAppliedSeed(string seedDigest)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT seed_digest, phase, applied_at, correlations_applied, correlations_skipped FROM applied_seeds WHERE seed_digest = @d";
        cmd.Parameters.AddWithValue("@d", seedDigest);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4));
    }

    public AppliedSeed? GetLatestAppliedSeed(string sessionId, string phase)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT seed_digest, phase, applied_at, correlations_applied, correlations_skipped
            FROM applied_seeds
            WHERE session_id = @sid AND phase = @phase
            ORDER BY applied_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@phase", phase);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new AppliedSeed(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetInt32(4));
    }

    public record SeedItem(int Id, string SeedDigest, string ItemType, string ItemKey, string AppliedAt, string? ConfirmedAt, int LocalMatchCount, string? RejectedAt);

    public void InsertSeedItem(string seedDigest, string itemType, string itemKey, string appliedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO seed_items (seed_digest, item_type, item_key, applied_at) VALUES (@d, @t, @k, @a)";
        cmd.Parameters.AddWithValue("@d", seedDigest);
        cmd.Parameters.AddWithValue("@t", itemType);
        cmd.Parameters.AddWithValue("@k", itemKey);
        cmd.Parameters.AddWithValue("@a", appliedAt);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SeedItem> GetSeedItems(string seedDigest)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, seed_digest, item_type, item_key, applied_at, confirmed_at, local_match_count, rejected_at FROM seed_items WHERE seed_digest = @d";
        cmd.Parameters.AddWithValue("@d", seedDigest);
        var items = new List<SeedItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            items.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return items;
    }

    public void ConfirmSeedItem(string seedDigest, string itemType, string itemKey, string confirmedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE seed_items SET confirmed_at = COALESCE(confirmed_at, @c), local_match_count = local_match_count + 1
            WHERE seed_digest = @d AND item_type = @t AND item_key = @k AND rejected_at IS NULL";
        cmd.Parameters.AddWithValue("@c", confirmedAt);
        cmd.Parameters.AddWithValue("@d", seedDigest);
        cmd.Parameters.AddWithValue("@t", itemType);
        cmd.Parameters.AddWithValue("@k", itemKey);
        cmd.ExecuteNonQuery();
    }

    public void RejectSeedItem(string seedDigest, string itemType, string itemKey, string rejectedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE seed_items SET rejected_at = @r WHERE seed_digest = @d AND item_type = @t AND item_key = @k AND confirmed_at IS NULL";
        cmd.Parameters.AddWithValue("@r", rejectedAt);
        cmd.Parameters.AddWithValue("@d", seedDigest);
        cmd.Parameters.AddWithValue("@t", itemType);
        cmd.Parameters.AddWithValue("@k", itemKey);
        cmd.ExecuteNonQuery();
    }

    public DateTimeOffset GetPhaseChangedAt(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT phase_changed_at FROM learning_session WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId);
        var result = cmd.ExecuteScalar();
        return result is string s ? DateTimeOffset.Parse(s) : DateTimeOffset.UtcNow;
    }

    public int GetUnseededCorrelationCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM correlated_actions WHERE session_id = @sid AND source = 'local'";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public double GetSeedConfirmationRatio(string seedDigest)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                CAST(SUM(CASE WHEN confirmed_at IS NOT NULL THEN 1 ELSE 0 END) AS REAL) /
                NULLIF(SUM(CASE WHEN rejected_at IS NULL THEN 1 ELSE 0 END), 0)
            FROM seed_items WHERE seed_digest = @d";
        cmd.Parameters.AddWithValue("@d", seedDigest);
        var result = cmd.ExecuteScalar();
        return result is double d ? d : 0.0;
    }

    public IDisposable BeginTransaction()
    {
        return _conn.BeginTransaction();
    }

    public void CommitTransaction(IDisposable txn)
    {
        if (txn is SqliteTransaction t) t.Commit();
    }

    // ── Universal Observation ──

    public void InsertAppSession(string sessionId, string appName, string? windowTitleHash,
        DateTimeOffset startTs, long focusMs, string? precedingApp)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_sessions (session_id, app_name, window_title_hash, start_ts, focus_ms, preceding_app)
            VALUES (@sid, @app, @title, @start, @focus, @prev)
        """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@app", appName);
        cmd.Parameters.AddWithValue("@title", (object?)windowTitleHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@start", startTs.ToString("o"));
        cmd.Parameters.AddWithValue("@focus", focusMs);
        cmd.Parameters.AddWithValue("@prev", (object?)precedingApp ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public void UpsertTemporalProfile(string sessionId, string periodType, string periodKey,
        int actionVolume, double peakLoadScore)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO temporal_profiles (session_id, period_type, period_key, action_volume, peak_load_score, updated_at)
            VALUES (@sid, @type, @key, @vol, @peak, datetime('now'))
            ON CONFLICT(session_id, period_type, period_key) DO UPDATE SET
                action_volume = action_volume + @vol,
                peak_load_score = MAX(peak_load_score, @peak),
                updated_at = datetime('now')
        """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@type", periodType);
        cmd.Parameters.AddWithValue("@key", periodKey);
        cmd.Parameters.AddWithValue("@vol", actionVolume);
        cmd.Parameters.AddWithValue("@peak", peakLoadScore);
        cmd.ExecuteNonQuery();
    }

    public void InsertStationProfile(string machineHash, int processorCount, int ramBucketGb,
        int monitorCount, string osVersion, string profileJson)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO station_profiles (machine_hash, processor_count, ram_bucket_gb, monitor_count, os_version, profile_json)
            VALUES (@hash, @cpu, @ram, @mon, @os, @json)
        """;
        cmd.Parameters.AddWithValue("@hash", machineHash);
        cmd.Parameters.AddWithValue("@cpu", processorCount);
        cmd.Parameters.AddWithValue("@ram", ramBucketGb);
        cmd.Parameters.AddWithValue("@mon", monitorCount);
        cmd.Parameters.AddWithValue("@os", osVersion);
        cmd.Parameters.AddWithValue("@json", profileJson);
        cmd.ExecuteNonQuery();
    }

    // ── Document Profiles ──

    public void UpsertDocumentProfile(string sessionId, string docHash, string? fileType,
        string? schemaFingerprint, int columnCount, string? rowCountBucket, string? category)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO document_profiles (session_id, doc_hash, file_type, schema_fingerprint,
                column_count, row_count_bucket, category, last_touched, touch_count)
            VALUES (@sid, @hash, @type, @schema, @cols, @rows, @cat, datetime('now'), 1)
            ON CONFLICT(session_id, doc_hash) DO UPDATE SET
                last_touched = datetime('now'),
                touch_count = touch_count + 1,
                schema_fingerprint = COALESCE(@schema, schema_fingerprint),
                column_count = COALESCE(@cols, column_count)
        """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@hash", docHash);
        cmd.Parameters.AddWithValue("@type", (object?)fileType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@schema", (object?)schemaFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cols", columnCount);
        cmd.Parameters.AddWithValue("@rows", (object?)rowCountBucket ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@cat", (object?)category ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ── Business Meta ──

    public void UpsertBusinessMeta(string businessId, string industry, string? detectedApps,
        string? stationRole, string? agentVersion, string? learningPhase)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO business_meta (business_id, industry, detected_apps, station_role,
                agent_version, learning_phase)
            VALUES (@bid, @ind, @apps, @role, @ver, @phase)
            ON CONFLICT(business_id) DO UPDATE SET
                industry = @ind,
                detected_apps = COALESCE(@apps, detected_apps),
                station_role = COALESCE(@role, station_role),
                agent_version = @ver,
                learning_phase = @phase
        """;
        cmd.Parameters.AddWithValue("@bid", businessId);
        cmd.Parameters.AddWithValue("@ind", industry);
        cmd.Parameters.AddWithValue("@apps", (object?)detectedApps ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@role", (object?)stationRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ver", (object?)agentVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@phase", (object?)learningPhase ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ── Readiness timing pipeline ──────────────────────────────────────

    public void InsertReadinessSample(string sessionId, string rxNumberHash,
        DateTimeOffset? enteredAt, DateTimeOffset? filledAt, DateTimeOffset? verifiedAt,
        DateTimeOffset? readyAt, DateTimeOffset? pickedUpAt,
        double? elapsedMinutes, int dayOfWeek, int hourOfDay,
        bool isControlled, int concurrentQueueDepth)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO readiness_samples (session_id, rx_number_hash, entered_at, filled_at,
                verified_at, ready_at, picked_up_at, elapsed_minutes, day_of_week, hour_of_day,
                is_controlled, concurrent_queue_depth)
            VALUES (@sid, @rx, @entered, @filled, @verified, @ready, @picked, @elapsed,
                @dow, @hour, @controlled, @depth)
        """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@rx", rxNumberHash);
        cmd.Parameters.AddWithValue("@entered", (object?)enteredAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@filled", (object?)filledAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@verified", (object?)verifiedAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ready", (object?)readyAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@picked", (object?)pickedUpAt?.ToString("o") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@elapsed", (object?)elapsedMinutes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dow", dayOfWeek);
        cmd.Parameters.AddWithValue("@hour", hourOfDay);
        cmd.Parameters.AddWithValue("@controlled", isControlled ? 1 : 0);
        cmd.Parameters.AddWithValue("@depth", concurrentQueueDepth);
        cmd.ExecuteNonQuery();
    }

    public (double AvgMinutes, double StdDevMinutes, int SampleCount) GetReadinessStats(int dayOfWeek, int hourOfDay)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT AVG(elapsed_minutes),
                   SQRT(AVG(elapsed_minutes * elapsed_minutes) - AVG(elapsed_minutes) * AVG(elapsed_minutes)),
                   COUNT(*)
            FROM readiness_samples
            WHERE day_of_week = @dow AND hour_of_day = @hour AND elapsed_minutes IS NOT NULL
        """;
        cmd.Parameters.AddWithValue("@dow", dayOfWeek);
        cmd.Parameters.AddWithValue("@hour", hourOfDay);
        using var reader = cmd.ExecuteReader();
        if (reader.Read() && !reader.IsDBNull(0))
            return (reader.GetDouble(0), reader.IsDBNull(1) ? 0 : reader.GetDouble(1), reader.GetInt32(2));
        return (0, 0, 0);
    }

}
