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
    // ── Learning Audit ──

    public void AppendLearningAudit(string sessionId, string observer, string action,
        string? target, bool phiScrubbed)
    {
        // Codex 2026-04-27 review (CRITICAL): same race shape as
        // AppendChainedAuditEntry. SELECT prev_hash + INSERT must be
        // serialized or two writers see the same prev and corrupt the
        // chain. Reuse _auditWriteLock — single audit-chain integrity
        // primitive guarding both audit_entries and learning_audit on
        // this connection.
        lock (_auditWriteLock)
        {
            using var tx = _conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            var now = DateTimeOffset.UtcNow.ToString("o");
            string? prevHash;

            using (var hashCmd = _conn.CreateCommand())
            {
                hashCmd.Transaction = tx;
                hashCmd.CommandText = "SELECT prev_hash FROM learning_audit WHERE session_id = @sid ORDER BY id DESC LIMIT 1";
                hashCmd.Parameters.AddWithValue("@sid", sessionId);
                prevHash = hashCmd.ExecuteScalar() as string;
            }

            var chainInput = $"{sessionId}|{observer}|{action}|{target}|{now}|{prevHash}";
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(chainInput))).ToLowerInvariant();

            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO learning_audit (session_id, observer, action, target, phi_scrubbed, timestamp, prev_hash)
                VALUES (@sid, @obs, @act, @target, @phi, @now, @hash)
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            cmd.Parameters.AddWithValue("@obs", observer);
            cmd.Parameters.AddWithValue("@act", action);
            cmd.Parameters.AddWithValue("@target", (object?)target ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@phi", phiScrubbed ? 1 : 0);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.Parameters.AddWithValue("@hash", hash);
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public int GetLearningAuditCount(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM learning_audit WHERE session_id = @sid";
        cmd.Parameters.AddWithValue("@sid", sessionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Codex 2026-04-27 review — verify the per-session learning_audit
    /// chain. Walks rows in id order; each row's prev_hash must equal the
    /// hash of the prior row (or null for the first). Reads under
    /// <see cref="_auditWriteLock"/> + materializes rows so verification
    /// observes a consistent snapshot.
    /// </summary>
    public bool VerifyLearningAuditChain(string sessionId)
    {
        var rows = new List<(string Observer, string Action, string? Target,
            bool PhiScrubbed, string Timestamp, string Hash)>();
        lock (_auditWriteLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT observer, action, target, phi_scrubbed, timestamp, prev_hash
                FROM learning_audit WHERE session_id = @sid ORDER BY id ASC
                """;
            cmd.Parameters.AddWithValue("@sid", sessionId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetInt32(3) == 1,
                    reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5)));
            }
        }

        string? expectedPrev = null;
        foreach (var row in rows)
        {
            var chainInput = $"{sessionId}|{row.Observer}|{row.Action}|{row.Target}|{row.Timestamp}|{expectedPrev}";
            var expectedHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(chainInput))).ToLowerInvariant();
            if (row.Hash != expectedHash) return false;
            expectedPrev = expectedHash;
        }
        return true;
    }

    // ── Rx Queue Candidates ──

    public void InsertRxQueueCandidate(string sessionId, string primaryTable,
        string? rxNumberColumn, string? statusColumn, string? dateColumn,
        string? patientFkColumn, double confidence, string evidenceJson,
        string? negativeEvidenceJson = null)
    {
        InvalidateLearnedTemplateEvidence(sessionId);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rx_queue_candidates
                (session_id, primary_table, rx_number_column, status_column,
                 date_column, patient_fk_column, confidence, evidence_json,
                 negative_evidence_json, stability_days, discovered_at)
            VALUES (@sid, @tbl, @rxCol, @statusCol, @dateCol, @patientCol,
                    @conf, @evidence, @negEvidence, 0, @now)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tbl", primaryTable);
        cmd.Parameters.AddWithValue("@rxCol", (object?)rxNumberColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@statusCol", (object?)statusColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dateCol", (object?)dateColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@patientCol", (object?)patientFkColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@evidence", evidenceJson);
        cmd.Parameters.AddWithValue("@negEvidence", (object?)negativeEvidenceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string PrimaryTable, string? RxNumberColumn, string? StatusColumn,
        string? DateColumn, string? PatientFkColumn, double Confidence, string EvidenceJson)>
        GetRxQueueCandidates(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT primary_table, rx_number_column, status_column, date_column,
                   patient_fk_column, confidence, evidence_json
            FROM rx_queue_candidates WHERE session_id = @sid
            ORDER BY confidence DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string?, string?, string?, string?, double, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDouble(5),
                reader.GetString(6)));
        }
        return results;
    }

    // ── Discovered Statuses ──

    public void InsertDiscoveredStatus(string sessionId, string schemaTable,
        string statusColumn, string statusValue, string? inferredMeaning,
        int transitionOrder, int occurrenceCount, double confidence)
    {
        InvalidateLearnedTemplateEvidence(sessionId);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discovered_statuses
                (session_id, schema_table, status_column, status_value,
                 inferred_meaning, transition_order, occurrence_count, confidence, discovered_at)
            VALUES (@sid, @tbl, @col, @val, @meaning, @order, @count, @conf, @now)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tbl", schemaTable);
        cmd.Parameters.AddWithValue("@col", statusColumn);
        cmd.Parameters.AddWithValue("@val", statusValue);
        cmd.Parameters.AddWithValue("@meaning", (object?)inferredMeaning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@order", transitionOrder);
        cmd.Parameters.AddWithValue("@count", occurrenceCount);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string StatusValue, string? InferredMeaning, int TransitionOrder, double Confidence)>
        GetDiscoveredStatuses(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT status_value, inferred_meaning, transition_order, confidence
            FROM discovered_statuses WHERE session_id = @sid
            ORDER BY transition_order
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string?, int, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetDouble(3)));
        }
        return results;
    }

    public IReadOnlyList<(string StatusValue, string? InferredMeaning, int TransitionOrder, double Confidence)>
        GetDiscoveredStatusesForTable(string sessionId, string schemaTable)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT status_value, inferred_meaning, transition_order, confidence
            FROM discovered_statuses WHERE session_id = @sid AND schema_table = @tbl
            ORDER BY transition_order
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tbl", schemaTable);
        var results = new List<(string, string?, int, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetDouble(3)));
        }
        return results;
    }

}
