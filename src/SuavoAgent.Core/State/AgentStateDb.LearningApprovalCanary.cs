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
    // ── Approval Digest & POM Snapshot (CRITICAL-5, CRITICAL-6) ──

    public void SetApprovalDigest(string sessionId, string digest, string approvedBy)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learning_session
            SET approved_model_digest = @digest,
                approved_at = @now,
                approved_by = @approvedBy
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@digest", digest);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("@approvedBy", approvedBy);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public sealed record LearningApprovalRow(
        string SessionId,
        string PharmacyId,
        string Phase,
        string? ModelDigest,
        string? ApprovedBy,
        string? ApprovedAt);

    public LearningApprovalRow? GetLearningApproval(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, pharmacy_id, phase, approved_model_digest, approved_by, approved_at
            FROM learning_session
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@id", sessionId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new LearningApprovalRow(
            SessionId: reader.GetString(0),
            PharmacyId: reader.GetString(1),
            Phase: reader.GetString(2),
            ModelDigest: reader.IsDBNull(3) ? null : reader.GetString(3),
            ApprovedBy: reader.IsDBNull(4) ? null : reader.GetString(4),
            ApprovedAt: reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public sealed record LearnedAdapterActivationRow(
        string PharmacyId,
        string SessionId,
        string TemplateDigest,
        string ModelDigest,
        string ApprovedBy,
        string ApprovedAt,
        string ActivatedAt,
        string Status,
        string? DeactivatedAt,
        string? DeactivationReason);

    public void UpsertLearnedAdapterActivation(
        string pharmacyId,
        string sessionId,
        string templateDigest,
        string modelDigest,
        string approvedBy,
        string approvedAt,
        string activatedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO learned_adapter_activations
                (pharmacy_id, session_id, template_digest, model_digest,
                 approved_by, approved_at, activated_at, status)
            VALUES
                (@pid, @sid, @template, @model, @by, @approvedAt, @activatedAt, 'active')
            ON CONFLICT(pharmacy_id) DO UPDATE SET
                session_id = @sid,
                template_digest = @template,
                model_digest = @model,
                approved_by = @by,
                approved_at = @approvedAt,
                activated_at = @activatedAt,
                status = 'active',
                deactivated_at = NULL,
                deactivation_reason = NULL
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@template", templateDigest);
        cmd.Parameters.AddWithValue("@model", modelDigest);
        cmd.Parameters.AddWithValue("@by", approvedBy);
        cmd.Parameters.AddWithValue("@approvedAt", approvedAt);
        cmd.Parameters.AddWithValue("@activatedAt", activatedAt);
        cmd.ExecuteNonQuery();
    }

    public LearnedAdapterActivationRow? GetLearnedAdapterActivation(string pharmacyId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT pharmacy_id, session_id, template_digest, model_digest,
                   approved_by, approved_at, activated_at, status,
                   deactivated_at, deactivation_reason
            FROM learned_adapter_activations
            WHERE pharmacy_id = @pid
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new LearnedAdapterActivationRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    public void DeactivateLearnedAdapter(string pharmacyId, string reason, string deactivatedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE learned_adapter_activations
            SET status = 'inactive', deactivated_at = @at, deactivation_reason = @reason
            WHERE pharmacy_id = @pid AND status = 'active'
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@at", deactivatedAt);
        cmd.Parameters.AddWithValue("@reason", reason);
        cmd.ExecuteNonQuery();
    }

    public void StorePomSnapshot(string sessionId, string pomJson)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE learning_session SET pom_snapshot = @pom WHERE id = @id";
        cmd.Parameters.AddWithValue("@pom", pomJson);
        cmd.Parameters.AddWithValue("@id", sessionId);
        cmd.ExecuteNonQuery();
    }

    public string? GetPomSnapshot(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT pom_snapshot FROM learning_session WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", sessionId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : (string)result;
    }

    // ── HMAC Salt (secret, per-session) ──

    /// <summary>
    /// Returns the per-session HMAC salt, generating a random 32-byte one on first call.
    /// This replaces AgentId (non-secret, sent in heartbeats) as the HMAC key for PHI hashing.
    /// </summary>
    public string GetOrCreateHmacSalt(string sessionId)
    {
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        // Atomic: only sets salt if currently NULL
        using var writeCmd = _conn.CreateCommand();
        writeCmd.CommandText = "UPDATE learning_session SET hmac_salt = COALESCE(hmac_salt, @salt) WHERE id = @id";
        writeCmd.Parameters.AddWithValue("@salt", salt);
        writeCmd.Parameters.AddWithValue("@id", sessionId);
        writeCmd.ExecuteNonQuery();

        // Read back the winner
        using var readCmd = _conn.CreateCommand();
        readCmd.CommandText = "SELECT hmac_salt FROM learning_session WHERE id = @id";
        readCmd.Parameters.AddWithValue("@id", sessionId);
        return (string)readCmd.ExecuteScalar()!;
    }

    // ── Active Session Lookup (CRITICAL-7) ──

    public string? GetActiveSessionId(string pharmacyId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM learning_session
            WHERE pharmacy_id = @pid AND phase NOT IN ('decommissioned', 'terminated', 'failed')
            ORDER BY started_at DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : (string)result;
    }

    // ── Canary Baselines ──

    public void UpsertCanaryBaseline(string pharmacyId, ContractBaseline baseline)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_canary_baselines
                (pharmacy_id, adapter_type, object_fingerprint, status_map_fingerprint,
                 query_fingerprint, result_shape_fingerprint, contract_fingerprint,
                 contract_json, schema_epoch, contract_version, created_at, updated_at)
            VALUES
                (@pid, @adapter, @obj, @stat, @qry, @shape, @contract,
                 @json, @epoch, @version, @now, @now)
            ON CONFLICT(pharmacy_id, adapter_type) DO UPDATE SET
                object_fingerprint = @obj,
                status_map_fingerprint = @stat,
                query_fingerprint = @qry,
                result_shape_fingerprint = @shape,
                contract_fingerprint = @contract,
                contract_json = @json,
                schema_epoch = @epoch,
                contract_version = @version,
                updated_at = @now
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", baseline.AdapterType);
        cmd.Parameters.AddWithValue("@obj", baseline.ObjectFingerprint);
        cmd.Parameters.AddWithValue("@stat", baseline.StatusMapFingerprint);
        cmd.Parameters.AddWithValue("@qry", baseline.QueryFingerprint);
        cmd.Parameters.AddWithValue("@shape", baseline.ResultShapeFingerprint);
        cmd.Parameters.AddWithValue("@contract", baseline.ContractFingerprint);
        cmd.Parameters.AddWithValue("@json", baseline.ContractJson);
        cmd.Parameters.AddWithValue("@epoch", baseline.SchemaEpoch);
        cmd.Parameters.AddWithValue("@version", baseline.ContractVersion);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public ContractBaseline? GetCanaryBaseline(string pharmacyId, string adapterType)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT adapter_type, object_fingerprint, status_map_fingerprint,
                   query_fingerprint, result_shape_fingerprint, contract_fingerprint,
                   contract_json, schema_epoch, contract_version
            FROM schema_canary_baselines
            WHERE pharmacy_id = @pid AND adapter_type = @adapter
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new ContractBaseline(
            AdapterType: reader.GetString(0),
            ObjectFingerprint: reader.GetString(1),
            StatusMapFingerprint: reader.GetString(2),
            QueryFingerprint: reader.GetString(3),
            ResultShapeFingerprint: reader.GetString(4),
            ContractFingerprint: reader.GetString(5),
            ContractJson: reader.GetString(6),
            SchemaEpoch: reader.GetInt32(7),
            ContractVersion: reader.GetInt32(8));
    }

    /// <summary>
    /// Returns the most recent contract fingerprint for a pharmacy from the canary baselines.
    /// </summary>
    public string? GetLatestContractFingerprint(string pharmacyId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT contract_fingerprint FROM schema_canary_baselines
            WHERE pharmacy_id = @pid
            ORDER BY updated_at DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        var result = cmd.ExecuteScalar();
        return result is DBNull or null ? null : (string)result;
    }

    // ── Canary Incidents ──

    public void InsertCanaryIncident(string pharmacyId, string adapterType, string severity,
        string driftedComponents, string baselineFingerprint, string observedFingerprint,
        string? details, int? droppedRowCount)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_canary_incidents
                (pharmacy_id, adapter_type, severity, drifted_components,
                 baseline_contract_fingerprint, observed_contract_fingerprint,
                 drift_details, dropped_batch_row_count, opened_at, last_seen_at)
            VALUES
                (@pid, @adapter, @severity, @drifted,
                 @baseline, @observed, @details, @dropped, @now, @now)
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        cmd.Parameters.AddWithValue("@severity", severity);
        cmd.Parameters.AddWithValue("@drifted", driftedComponents);
        cmd.Parameters.AddWithValue("@baseline", baselineFingerprint);
        cmd.Parameters.AddWithValue("@observed", observedFingerprint);
        cmd.Parameters.AddWithValue("@details", (object?)details ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dropped", (object?)droppedRowCount ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string Severity, int? DroppedBatchRowCount, string OpenedAt)>
        GetOpenCanaryIncidents(string pharmacyId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT severity, dropped_batch_row_count, opened_at
            FROM schema_canary_incidents
            WHERE pharmacy_id = @pid AND resolved_at IS NULL
            ORDER BY opened_at ASC
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        var results = new List<(string, int?, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int? dropped = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            results.Add((reader.GetString(0), dropped, reader.GetString(2)));
        }
        return results;
    }

    // ── Canary Hold ──

    public void UpsertCanaryHold(string pharmacyId, string adapterType, string severity, string baselineFingerprint)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_canary_hold
                (pharmacy_id, adapter_type, severity, drift_hold_since,
                 blocked_cycle_count, last_seen_at, baseline_contract_fingerprint)
            VALUES (@pid, @adapter, @severity, @now, 0, @now, @baseline)
            ON CONFLICT(pharmacy_id, adapter_type) DO UPDATE SET
                severity = @severity,
                last_seen_at = @now,
                baseline_contract_fingerprint = @baseline
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        cmd.Parameters.AddWithValue("@severity", severity);
        cmd.Parameters.AddWithValue("@baseline", baselineFingerprint);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public void IncrementCanaryHoldCycles(string pharmacyId, string adapterType)
    {
        var now = DateTimeOffset.UtcNow.ToString("o");
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE schema_canary_hold
            SET blocked_cycle_count = blocked_cycle_count + 1,
                last_seen_at = @now
            WHERE pharmacy_id = @pid AND adapter_type = @adapter
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    public (string Severity, int BlockedCycles, string DriftHoldSince)? GetCanaryHold(string pharmacyId, string adapterType)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT severity, blocked_cycle_count, drift_hold_since
            FROM schema_canary_hold
            WHERE pharmacy_id = @pid AND adapter_type = @adapter
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), reader.GetInt32(1), reader.GetString(2));
    }

    public void ClearCanaryHold(string pharmacyId, string adapterType)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM schema_canary_hold
            WHERE pharmacy_id = @pid AND adapter_type = @adapter
            """;
        cmd.Parameters.AddWithValue("@pid", pharmacyId);
        cmd.Parameters.AddWithValue("@adapter", adapterType);
        cmd.ExecuteNonQuery();
    }

}
