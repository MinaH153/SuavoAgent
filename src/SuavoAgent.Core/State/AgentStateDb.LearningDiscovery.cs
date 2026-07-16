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
    // ── Learning Session CRUD ──

    public void CreateLearningSession(string id, string pharmacyId)
    {
        using var cmd = _conn.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToString("o");
        cmd.CommandText = """
            INSERT INTO learning_session (id, pharmacy_id, phase, mode, started_at, phase_changed_at)
            VALUES (@id, @pharmacyId, 'discovery', 'observer', @now, @now)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@pharmacyId", pharmacyId);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public (string Id, string PharmacyId, string Phase, string Mode,
            string? ApprovedModelDigest, int SchemaEpoch,
            int SupervisedSuccessCount, int SupervisedCorrectionCount)?
        GetLearningSession(string id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, pharmacy_id, phase, mode, approved_model_digest,
                   schema_epoch, supervised_success_count, supervised_correction_count
            FROM learning_session WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (
            reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7));
    }

    public void UpdateLearningPhase(string sessionId, string phase)
    {
        var session = GetLearningSession(sessionId);
        if (session is null)
            throw new InvalidOperationException($"Learning session '{sessionId}' not found");

        if (!LearningSession.IsValidPhaseTransition(session.Value.Phase, phase))
            throw new InvalidOperationException(
                $"Invalid phase transition: '{session.Value.Phase}' → '{phase}'");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learning_session SET phase = @phase, phase_changed_at = @now WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@phase", phase);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateLearningMode(string sessionId, string mode)
    {
        var session = GetLearningSession(sessionId);
        if (session is null)
            throw new InvalidOperationException($"Learning session '{sessionId}' not found");

        if (!LearningSession.IsValidModeTransition(session.Value.Mode, mode))
            throw new InvalidOperationException(
                $"Invalid mode transition: '{session.Value.Mode}' → '{mode}'");

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learning_session SET mode = @mode WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@mode", mode);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    // ── Observed Processes ──

    public void UpsertObservedProcess(string sessionId, string processName, string exePath,
        string? windowTitleScrubbed = null, bool isPmsCandidate = false,
        string? windowTitleHash = null, string? parentProcess = null, bool isService = false)
    {
        using var cmd = _conn.CreateCommand();
        var now = DateTimeOffset.UtcNow.ToString("o");
        cmd.CommandText = """
            INSERT INTO observed_processes
                (session_id, process_name, exe_path, window_title_hash, window_title_scrubbed,
                 parent_process, is_service, is_pms_candidate, first_seen, last_seen, occurrence_count)
            VALUES (@sid, @name, @path, @titleHash, @titleScrub, @parent, @isSvc, @isPms, @now, @now, 1)
            ON CONFLICT(session_id, process_name, exe_path) DO UPDATE SET
                last_seen = @now,
                occurrence_count = occurrence_count + 1,
                window_title_scrubbed = COALESCE(@titleScrub, window_title_scrubbed),
                window_title_hash = COALESCE(@titleHash, window_title_hash),
                is_pms_candidate = MAX(is_pms_candidate, @isPms)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@name", processName);
        cmd.Parameters.AddWithValue("@path", exePath);
        cmd.Parameters.AddWithValue("@titleHash", (object?)windowTitleHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@titleScrub", (object?)windowTitleScrubbed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@parent", (object?)parentProcess ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@isSvc", isService ? 1 : 0);
        cmd.Parameters.AddWithValue("@isPms", isPmsCandidate ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string ProcessName, string ExePath, string? WindowTitleScrubbed,
        int OccurrenceCount, bool IsPmsCandidate)> GetObservedProcesses(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT process_name, exe_path, window_title_scrubbed, occurrence_count, is_pms_candidate
            FROM observed_processes WHERE session_id = @sid ORDER BY occurrence_count DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string, string?, int, bool)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4) == 1));
        }
        return results;
    }

    // ── Discovered Schemas ──

    public void BeginDiscoveredSchemaSnapshot(
        string sessionId,
        string sourceIdentityDigest,
        string databaseName)
    {
        lock (_connLock)
        {
            using var tx = _conn.BeginTransaction();
            using (var clearSchemas = _conn.CreateCommand())
            {
                clearSchemas.Transaction = tx;
                clearSchemas.CommandText = "DELETE FROM discovered_schemas WHERE session_id = @sid";
                clearSchemas.Parameters.AddWithValue("@sid", sessionId);
                clearSchemas.ExecuteNonQuery();
            }
            using (var clearUnique = _conn.CreateCommand())
            {
                clearUnique.Transaction = tx;
                clearUnique.CommandText = "DELETE FROM discovered_unique_columns WHERE session_id = @sid";
                clearUnique.Parameters.AddWithValue("@sid", sessionId);
                clearUnique.ExecuteNonQuery();
            }
            using (var clearCandidates = _conn.CreateCommand())
            {
                clearCandidates.Transaction = tx;
                clearCandidates.CommandText = "DELETE FROM rx_queue_candidates WHERE session_id = @sid";
                clearCandidates.Parameters.AddWithValue("@sid", sessionId);
                clearCandidates.ExecuteNonQuery();
            }
            using (var clearStatuses = _conn.CreateCommand())
            {
                clearStatuses.Transaction = tx;
                clearStatuses.CommandText = "DELETE FROM discovered_statuses WHERE session_id = @sid";
                clearStatuses.Parameters.AddWithValue("@sid", sessionId);
                clearStatuses.ExecuteNonQuery();
            }
            using (var snapshot = _conn.CreateCommand())
            {
                snapshot.Transaction = tx;
                snapshot.CommandText = """
                    INSERT INTO schema_discovery_snapshots (
                        session_id, source_identity_digest, database_name,
                        schema_contract_digest, fk_discovery_complete,
                        template_evidence_digest, template_evidence_complete, discovered_at)
                    VALUES (@sid, @source, @db, NULL, 0, NULL, 0, @now)
                    ON CONFLICT(session_id) DO UPDATE SET
                        source_identity_digest = excluded.source_identity_digest,
                        database_name = excluded.database_name,
                        schema_contract_digest = NULL,
                        fk_discovery_complete = 0,
                        template_evidence_digest = NULL,
                        template_evidence_complete = 0,
                        discovered_at = excluded.discovered_at
                    """;
                snapshot.Parameters.AddWithValue("@sid", sessionId);
                snapshot.Parameters.AddWithValue("@source", sourceIdentityDigest);
                snapshot.Parameters.AddWithValue("@db", databaseName);
                snapshot.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
                snapshot.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void InsertDiscoveredUniqueColumn(
        string sessionId,
        string schemaName,
        string tableName,
        string columnName)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discovered_unique_columns (
                session_id, schema_name, table_name, column_name, discovered_at)
            VALUES (@sid, @schema, @table, @column, @now)
            ON CONFLICT(session_id, schema_name, table_name, column_name) DO UPDATE SET
                discovered_at = excluded.discovered_at
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@table", tableName);
        cmd.Parameters.AddWithValue("@column", columnName);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlySet<string> GetDiscoveredUniqueColumns(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT schema_name, table_name, column_name
            FROM discovered_unique_columns
            WHERE session_id = @sid
            ORDER BY schema_name, table_name, column_name
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new HashSet<string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add($"{reader.GetString(0)}.{reader.GetString(1)}.{reader.GetString(2)}");
        return results;
    }

    public void CompleteDiscoveredSchemaSnapshot(string sessionId)
    {
        var digest = ComputeDiscoveredSchemaContractDigest(sessionId);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE schema_discovery_snapshots
            SET schema_contract_digest = @digest,
                fk_discovery_complete = 1,
                template_evidence_digest = NULL,
                template_evidence_complete = 0,
                discovered_at = @now
            WHERE session_id = @sid
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidDataException("Schema discovery snapshot could not be completed.");
    }

    public void InvalidateDiscoveredSchemaSnapshot(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE schema_discovery_snapshots
            SET schema_contract_digest = NULL,
                fk_discovery_complete = 0,
                template_evidence_digest = NULL,
                template_evidence_complete = 0
            WHERE session_id = @sid
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    public (string SourceIdentityDigest, string DatabaseName, string SchemaContractDigest)?
        GetCompleteDiscoveredSchemaSnapshot(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT source_identity_digest, database_name, schema_contract_digest,
                   template_evidence_digest
            FROM schema_discovery_snapshots
            WHERE session_id = @sid
              AND fk_discovery_complete = 1
              AND schema_contract_digest IS NOT NULL
              AND template_evidence_complete = 1
              AND template_evidence_digest IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var result = (reader.GetString(0), reader.GetString(1), reader.GetString(2));
        var expectedEvidenceDigest = reader.GetString(3);
        if (reader.Read())
            throw new InvalidDataException("Schema discovery snapshot identity is ambiguous.");
        reader.Dispose();
        if (!string.Equals(
                expectedEvidenceDigest,
                ComputeLearnedTemplateEvidenceDigest(sessionId),
                StringComparison.Ordinal))
            return null;
        return result;
    }

    public void CompleteLearnedTemplateEvidence(string sessionId)
    {
        var digest = ComputeLearnedTemplateEvidenceDigest(sessionId);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE schema_discovery_snapshots
            SET template_evidence_digest = @digest,
                template_evidence_complete = 1,
                discovered_at = @now
            WHERE session_id = @sid
              AND fk_discovery_complete = 1
              AND schema_contract_digest IS NOT NULL
              AND EXISTS (
                SELECT 1 FROM rx_queue_candidates WHERE session_id = @sid)
              AND EXISTS (
                SELECT 1 FROM discovered_statuses WHERE session_id = @sid)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidDataException("Learned template evidence is incomplete.");
    }

    public void InvalidateLearnedTemplateEvidence(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE schema_discovery_snapshots
            SET template_evidence_digest = NULL,
                template_evidence_complete = 0
            WHERE session_id = @sid
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.ExecuteNonQuery();
    }

    public string ComputeLearnedTemplateEvidenceDigest(string sessionId)
    {
        var canonical = new StringBuilder();
        using (var snapshot = _conn.CreateCommand())
        {
            snapshot.CommandText = """
                SELECT COALESCE(schema_contract_digest, '')
                FROM schema_discovery_snapshots
                WHERE session_id = @sid
                """;
            snapshot.Parameters.AddWithValue("@sid", sessionId);
            canonical.Append(snapshot.ExecuteScalar()?.ToString() ?? "").Append('\n');
        }
        using (var candidates = _conn.CreateCommand())
        {
            candidates.CommandText = """
                SELECT primary_table, COALESCE(rx_number_column, ''),
                       COALESCE(status_column, ''), COALESCE(date_column, ''),
                       COALESCE(patient_fk_column, ''), confidence, evidence_json,
                       COALESCE(negative_evidence_json, '')
                FROM rx_queue_candidates
                WHERE session_id = @sid
                ORDER BY confidence DESC, primary_table, rx_number_column, status_column
                """;
            candidates.Parameters.AddWithValue("@sid", sessionId);
            using var reader = candidates.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    canonical.Append(reader.GetValue(i)).Append('|');
                canonical.Append('\n');
            }
        }
        canonical.Append("--statuses--\n");
        using (var statuses = _conn.CreateCommand())
        {
            statuses.CommandText = """
                SELECT schema_table, status_column, status_value,
                       COALESCE(inferred_meaning, ''), transition_order,
                       occurrence_count, confidence
                FROM discovered_statuses
                WHERE session_id = @sid
                ORDER BY schema_table, status_column, transition_order, status_value
                """;
            statuses.Parameters.AddWithValue("@sid", sessionId);
            using var reader = statuses.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    canonical.Append(reader.GetValue(i)).Append('|');
                canonical.Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    public string ComputeDiscoveredSchemaContractDigest(string sessionId)
    {
        var canonical = new StringBuilder();
        using (var schema = _conn.CreateCommand())
        {
            schema.CommandText = """
                SELECT schema_name, table_name, column_name, data_type,
                       COALESCE(max_length, -1), is_nullable, is_pk, is_fk,
                       COALESCE(fk_target_table, ''), COALESCE(fk_target_column, '')
                FROM discovered_schemas
                WHERE session_id = @sid
                ORDER BY schema_name, table_name, column_name
                """;
            schema.Parameters.AddWithValue("@sid", sessionId);
            using var reader = schema.ExecuteReader();
            while (reader.Read())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    canonical.Append(reader.GetValue(i)).Append('|');
                canonical.Append('\n');
            }
        }
        canonical.Append("--unique--\n");
        using (var unique = _conn.CreateCommand())
        {
            unique.CommandText = """
                SELECT schema_name, table_name, column_name
                FROM discovered_unique_columns
                WHERE session_id = @sid
                ORDER BY schema_name, table_name, column_name
                """;
            unique.Parameters.AddWithValue("@sid", sessionId);
            using var reader = unique.ExecuteReader();
            while (reader.Read())
                canonical.Append(reader.GetString(0)).Append('.')
                    .Append(reader.GetString(1)).Append('.')
                    .Append(reader.GetString(2)).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    public void InsertDiscoveredSchema(string sessionId, string serverHash,
        string databaseName, string schemaName, string tableName, string columnName,
        string dataType, int? maxLength, bool isNullable, bool isPk, bool isFk,
        string? fkTargetTable, string? fkTargetColumn, string? inferredPurpose)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discovered_schemas
                (session_id, server_hash, database_name, schema_name, table_name,
                 column_name, data_type, max_length, is_nullable, is_pk, is_fk,
                 fk_target_table, fk_target_column, inferred_purpose, discovered_at)
            VALUES (@sid, @svr, @db, @schema, @tbl, @col, @dtype, @maxLen,
                    @nullable, @pk, @fk, @fkTbl, @fkCol, @purpose, @now)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@svr", serverHash);
        cmd.Parameters.AddWithValue("@db", databaseName);
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@tbl", tableName);
        cmd.Parameters.AddWithValue("@col", columnName);
        cmd.Parameters.AddWithValue("@dtype", dataType);
        cmd.Parameters.AddWithValue("@maxLen", (object?)maxLength ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@nullable", isNullable ? 1 : 0);
        cmd.Parameters.AddWithValue("@pk", isPk ? 1 : 0);
        cmd.Parameters.AddWithValue("@fk", isFk ? 1 : 0);
        cmd.Parameters.AddWithValue("@fkTbl", (object?)fkTargetTable ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fkCol", (object?)fkTargetColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@purpose", (object?)inferredPurpose ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string SchemaName, string TableName, string ColumnName,
        string DataType, bool IsPk, bool IsFk, string? InferredPurpose)>
        GetDiscoveredSchemas(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT schema_name, table_name, column_name, data_type, is_pk, is_fk, inferred_purpose
            FROM discovered_schemas WHERE session_id = @sid
            ORDER BY schema_name, table_name, id
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string, string, string, bool, bool, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4) == 1, reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return results;
    }

    public void BindDiscoveredForeignKey(
        string sessionId,
        string schemaName,
        string tableName,
        string columnName,
        string targetSchema,
        string targetTable,
        string targetColumn)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE discovered_schemas
            SET is_fk = 1,
                fk_target_table = @targetTable,
                fk_target_column = @targetColumn
            WHERE session_id = @sid
              AND schema_name = @schema
              AND table_name = @table
              AND column_name = @column
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@table", tableName);
        cmd.Parameters.AddWithValue("@column", columnName);
        cmd.Parameters.AddWithValue("@targetTable", $"{targetSchema}.{targetTable}");
        cmd.Parameters.AddWithValue("@targetColumn", targetColumn);
        if (cmd.ExecuteNonQuery() < 1)
            throw new InvalidDataException("Discovered foreign key did not bind a schema column.");
    }

    public IReadOnlyList<(string SchemaName, string TableName, string ColumnName,
        string DataType, bool IsPk, bool IsFk, string? FkTargetTable,
        string? FkTargetColumn, string? InferredPurpose)>
        GetDiscoveredSchemaGraph(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT schema_name, table_name, column_name, data_type, is_pk, is_fk,
                   fk_target_table, fk_target_column, inferred_purpose
            FROM discovered_schemas WHERE session_id = @sid
            ORDER BY schema_name, table_name, id
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string, string, string, bool, bool, string?, string?, string?)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4) == 1, reader.GetInt32(5) == 1,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return results;
    }

}
