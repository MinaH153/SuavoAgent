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
    // ── Workflow Templates (v3.12) ──

    /// <summary>
    /// Upserts a WorkflowTemplate. Idempotency contract: when a template with the
    /// same <paramref name="templateId"/> already exists, only the observation
    /// count / aggregate confidence / retirement fields are refreshed — the
    /// steps_json and steps_hash must match exactly or the caller should have
    /// picked a different template_id (e.g. via version bump + retire).
    /// </summary>
    public void UpsertWorkflowTemplate(
        string templateId,
        string templateVersion,
        string skillId,
        string processNameGlob,
        string pmsVersionRangeJson,
        string screenSignature,
        string stepsHash,
        string? routineHashOrigin,
        string stepsJson,
        double aggregateConfidence,
        int observationCount,
        bool hasWriteback,
        string extractedAt,
        string extractedBy,
        bool captureOnly = false,
        string? sourceSessionId = null)
    {
        lock (_connLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
            INSERT INTO workflow_templates
                (template_id, template_version, skill_id, process_name_glob,
                 pms_version_range_json, screen_signature, steps_hash,
                 routine_hash_origin, steps_json, aggregate_confidence,
                 observation_count, has_writeback, extracted_at, extracted_by,
                 capture_only, source_session_id)
            VALUES
                (@id, @ver, @skill, @glob, @range, @screen, @sh,
                 @origin, @steps, @conf, @obs, @hw, @at, @by, @capture, @sid)
            ON CONFLICT(template_id) DO UPDATE SET
                observation_count = @obs,
                aggregate_confidence = @conf,
                extracted_at = @at,
                capture_only = @capture,
                source_session_id = @sid,
                retired_at = NULL,
                retirement_reason = NULL,
                consecutive_low_conf_runs = 0
            """;
            cmd.Parameters.AddWithValue("@id", templateId);
            cmd.Parameters.AddWithValue("@ver", templateVersion);
            cmd.Parameters.AddWithValue("@skill", skillId);
            cmd.Parameters.AddWithValue("@glob", processNameGlob);
            cmd.Parameters.AddWithValue("@range", pmsVersionRangeJson);
            cmd.Parameters.AddWithValue("@screen", screenSignature);
            cmd.Parameters.AddWithValue("@sh", stepsHash);
            cmd.Parameters.AddWithValue("@origin", (object?)routineHashOrigin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@steps", stepsJson);
            cmd.Parameters.AddWithValue("@conf", aggregateConfidence);
            cmd.Parameters.AddWithValue("@obs", observationCount);
            cmd.Parameters.AddWithValue("@hw", hasWriteback ? 1 : 0);
            cmd.Parameters.AddWithValue("@at", extractedAt);
            cmd.Parameters.AddWithValue("@by", extractedBy);
            cmd.Parameters.AddWithValue("@capture", captureOnly ? 1 : 0);
            cmd.Parameters.AddWithValue("@sid", (object?)sourceSessionId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public record WorkflowTemplateRow(
        string TemplateId, string TemplateVersion, string SkillId, string ProcessNameGlob,
        string PmsVersionRangeJson, string ScreenSignature, string StepsHash,
        string? RoutineHashOrigin, string StepsJson, double AggregateConfidence,
        int ObservationCount, bool HasWriteback, string ExtractedAt, string ExtractedBy,
        string? RetiredAt, string? RetirementReason, int ConsecutiveLowConfRuns,
        bool CaptureOnly, string? SourceSessionId);

    public WorkflowTemplateRow? GetWorkflowTemplate(string templateId)
    {
        lock (_connLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
            SELECT template_id, template_version, skill_id, process_name_glob,
                   pms_version_range_json, screen_signature, steps_hash,
                   routine_hash_origin, steps_json, aggregate_confidence,
                   observation_count, has_writeback, extracted_at, extracted_by,
                   retired_at, retirement_reason, consecutive_low_conf_runs,
                   capture_only, source_session_id
            FROM workflow_templates WHERE template_id = @id
            """;
            cmd.Parameters.AddWithValue("@id", templateId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return ReadTemplateRow(reader);
        }
    }

    public WorkflowTemplateRow? GetWorkflowTemplateByScreen(string skillId, string screenSignature)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT template_id, template_version, skill_id, process_name_glob,
                   pms_version_range_json, screen_signature, steps_hash,
                   routine_hash_origin, steps_json, aggregate_confidence,
                   observation_count, has_writeback, extracted_at, extracted_by,
                   retired_at, retirement_reason, consecutive_low_conf_runs,
                   capture_only, source_session_id
            FROM workflow_templates WHERE skill_id = @skill AND screen_signature = @screen
              AND retired_at IS NULL
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@skill", skillId);
        cmd.Parameters.AddWithValue("@screen", screenSignature);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return ReadTemplateRow(reader);
    }

    public IReadOnlyList<WorkflowTemplateRow> GetActiveWorkflowTemplates(string? skillId = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = skillId is null
            ? """
              SELECT template_id, template_version, skill_id, process_name_glob,
                     pms_version_range_json, screen_signature, steps_hash,
                     routine_hash_origin, steps_json, aggregate_confidence,
                     observation_count, has_writeback, extracted_at, extracted_by,
                     retired_at, retirement_reason, consecutive_low_conf_runs,
                     capture_only, source_session_id
              FROM workflow_templates WHERE retired_at IS NULL AND capture_only = 0
              ORDER BY extracted_at
              """
            : """
              SELECT template_id, template_version, skill_id, process_name_glob,
                     pms_version_range_json, screen_signature, steps_hash,
                     routine_hash_origin, steps_json, aggregate_confidence,
                     observation_count, has_writeback, extracted_at, extracted_by,
                     retired_at, retirement_reason, consecutive_low_conf_runs,
                     capture_only, source_session_id
              FROM workflow_templates WHERE retired_at IS NULL AND capture_only = 0 AND skill_id = @skill
              ORDER BY extracted_at
              """;
        if (skillId is not null) cmd.Parameters.AddWithValue("@skill", skillId);

        var rows = new List<WorkflowTemplateRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadTemplateRow(reader));
        return rows;
    }

    public void RetireWorkflowTemplate(string templateId, string retiredAt, string reason)
    {
        lock (_connLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
            UPDATE workflow_templates
               SET retired_at = @at, retirement_reason = @reason
             WHERE template_id = @id
            """;
            cmd.Parameters.AddWithValue("@id", templateId);
            cmd.Parameters.AddWithValue("@at", retiredAt);
            cmd.Parameters.AddWithValue("@reason", reason);
            cmd.ExecuteNonQuery();
            using var remove = _conn.CreateCommand();
            remove.CommandText =
                "DELETE FROM active_auto_rule_registry WHERE template_id = @id";
            remove.Parameters.AddWithValue("@id", templateId);
            remove.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Observe→assist bridge lookup: returns the single active (retired_at IS NULL) template whose ENTRY
    /// step (Steps[0].ExpectedVisible) structurally matches the current perceived screen, or null.
    ///
    /// SCREEN KEY: <see cref="PerceivedScreen.Signatures"/> ("controlType|automationId") matched against the
    /// template's entry <see cref="ElementSignature"/>s via <see cref="ElementSignature.MatchesStructurally"/>
    /// + MinElementsRequired — the SAME predicate the rule engine applies to a template's When
    /// fingerprints. We deliberately do NOT compare the stored <c>screen_signature</c> SHA: that hash is
    /// SHA-256 over the 3-part CanonicalRepr of the entry subset and is not reconstructible from a live
    /// perceived screen (className is dropped on the wire; the perceived set is the full screen, not the
    /// entry subset), so an equality lookup could NEVER match. Structural subset match is the real key.
    ///
    /// Skill scope: NOT filtered by skill_id — the navigate loop's skillId is the synthetic "navigate",
    /// whereas templates carry their extraction skillId, so a skill filter would never match. The matched
    /// template's own SkillId is used by the caller to derive the approval rule id.
    ///
    /// Fail-closed: empty/parseless perceived set → null; 0 matches → null; &gt;1 matches (ambiguous) → null;
    /// a single unrehydratable row is skipped (never throws to the caller).
    /// </summary>
    public WorkflowTemplate? FindActiveTemplateByScreenSignatures(IReadOnlyList<string> perceivedSignatures)
    {
        if (perceivedSignatures is null || perceivedSignatures.Count == 0) return null;

        // Parse "controlType|automationId" → match atoms. className is unknown on the wire (null), which
        // ElementSignature treats as null-tolerant, so a template that DOES carry a className still matches.
        var perceived = new List<ElementSignature>(perceivedSignatures.Count);
        foreach (var s in perceivedSignatures)
        {
            var parts = s.Split('|');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                continue;
            try { perceived.Add(new ElementSignature(parts[0], parts[1], null)); }
            catch (ArgumentException) { /* malformed atom — skip */ }
        }
        if (perceived.Count == 0) return null;

        // ponytail: linear scan over active templates (few per box); index later only if it ever matters.
        WorkflowTemplate? match = null;
        foreach (var row in GetActiveWorkflowTemplates())
        {
            WorkflowTemplate template;
            try { template = TemplateRuleGenerator.Rehydrate(row); }
            catch (Exception) { continue; } // fail-closed on a single bad row
            if (template.Steps.Count == 0) continue;
            if (!EntryScreenMatches(template.Steps[0], perceived)) continue;

            if (match is not null) return null; // ambiguous → fail-closed
            match = template;
        }
        return match;
    }

    private static bool EntryScreenMatches(TemplateStep entry, IReadOnlyList<ElementSignature> perceived)
    {
        var hits = 0;
        foreach (var expected in entry.ExpectedVisible)
            if (perceived.Any(p => expected.MatchesStructurally(p)))
                hits++;
        return hits >= entry.MinElementsRequired;
    }

    // --- M2b: learned selector patches (applied by the seed pipeline; read by the resolver) ---

    public void UpsertSelectorPatch(SelectorPatch patch, string appliedAt) =>
        UpsertSelectorPatchCore(patch, appliedAt, tx: null);

    /// <summary>
    /// Upserts the patch AND appends its chained audit entry in ONE atomic transaction. The operator
    /// direct-correction (update_selector) needs both to commit together — but AppendChainedAuditEntry
    /// owns its own Serializable transaction, so a caller cannot wrap the two in an OUTER transaction
    /// (Microsoft.Data.Sqlite forbids nested transactions — the field bug on Mina's box, 2026-06-03,
    /// where the handler's BeginTransaction + AppendChainedAuditEntry threw). Doing both writes here
    /// under the single _auditWriteLock + one transaction is the atomic path. Returns the new chain hash.
    /// </summary>
    public string UpsertSelectorPatchWithAudit(SelectorPatch patch, AuditEntry auditEntry, string appliedAt)
    {
        lock (_auditWriteLock)
        {
            using var tx = _conn.BeginTransaction(System.Data.IsolationLevel.Serializable);
            UpsertSelectorPatchCore(patch, appliedAt, tx);
            var newHash = AppendAuditEntryLocked(auditEntry, appliedAt, tx);
            tx.Commit();
            return newHash;
        }
    }

    private void UpsertSelectorPatchCore(SelectorPatch patch, string appliedAt, Microsoft.Data.Sqlite.SqliteTransaction? tx)
    {
        using var cmd = _conn.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO selector_patches
                (patch_id, skill_id, step_id, pms_fingerprint, screen_signature,
                 target_json, fallbacks_json, confidence, seed_digest, version, approved_by_role, applied_at)
            VALUES (@id, @skill, @step, @pms, @screen, @target, @fallbacks, @conf, @digest, @ver, @role, @at)
            """;
        cmd.Parameters.AddWithValue("@id", patch.PatchId);
        cmd.Parameters.AddWithValue("@skill", patch.SkillId);
        cmd.Parameters.AddWithValue("@step", patch.StepId.ToString());
        cmd.Parameters.AddWithValue("@pms", (object?)patch.PmsFingerprint ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@screen", (object?)patch.ScreenSignatureV1 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@target", System.Text.Json.JsonSerializer.Serialize(patch.Target));
        cmd.Parameters.AddWithValue("@fallbacks", System.Text.Json.JsonSerializer.Serialize(patch.Fallbacks));
        cmd.Parameters.AddWithValue("@conf", patch.Confidence);
        cmd.Parameters.AddWithValue("@digest", patch.SeedDigest);
        cmd.Parameters.AddWithValue("@ver", patch.Version);
        cmd.Parameters.AddWithValue("@role", (object?)patch.ApprovedByRole ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@at", appliedAt);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Active (non-retired) selector patches, newest version first.</summary>
    public IReadOnlyList<SelectorPatch> GetActiveSelectorPatches()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT patch_id, skill_id, step_id, pms_fingerprint, screen_signature,
                   target_json, fallbacks_json, confidence, seed_digest, version, approved_by_role
            FROM selector_patches WHERE retired_at IS NULL
            ORDER BY version DESC
            """;
        var rows = new List<SelectorPatch>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var patch = TryReadSelectorPatch(reader);
            if (patch is not null) rows.Add(patch);
        }
        return rows;
    }

    public void RetireSelectorPatch(string patchId, string retiredAt, string reason)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE selector_patches
               SET retired_at = @at, retirement_reason = @reason
             WHERE patch_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", patchId);
        cmd.Parameters.AddWithValue("@at", retiredAt);
        cmd.Parameters.AddWithValue("@reason", reason);
        cmd.ExecuteNonQuery();
    }

    // Tolerant read: a malformed row (e.g. a target with no AutomationId, which
    // ElementSignature rejects) is skipped rather than throwing — a corrupt patch
    // must never break the whole load + leave the resolver blind to the good ones.
    private static SelectorPatch? TryReadSelectorPatch(SqliteDataReader reader)
    {
        try
        {
            // A corrupt step_id must be skipped, NOT defaulted to step 0 (which would silently
            // re-target the patch to the wrong step).
            if (!Enum.TryParse<SelectorStepId>(reader.GetString(2), out var step)) return null;
            var target = System.Text.Json.JsonSerializer.Deserialize<ElementSignature>(reader.GetString(5));
            if (target is null) return null;
            var fallbacks = System.Text.Json.JsonSerializer
                .Deserialize<List<ElementSignature>>(reader.GetString(6)) ?? new List<ElementSignature>();
            return new SelectorPatch(
                PatchId: reader.GetString(0),
                SkillId: reader.GetString(1),
                StepId: step,
                PmsFingerprint: reader.IsDBNull(3) ? null : reader.GetString(3),
                ScreenSignatureV1: reader.IsDBNull(4) ? null : reader.GetString(4),
                Target: target,
                Fallbacks: fallbacks,
                Confidence: reader.GetDouble(7),
                SeedDigest: reader.GetString(8),
                Version: reader.GetInt32(9),
                ApprovedByRole: reader.IsDBNull(10) ? null : reader.GetString(10));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Increments the low-confidence counter for a template. Returns the new value.
    /// Extractor uses this to drive auto-retirement at a configured threshold.
    /// </summary>
    public int IncrementTemplateLowConfidenceRuns(string templateId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE workflow_templates
               SET consecutive_low_conf_runs = consecutive_low_conf_runs + 1
             WHERE template_id = @id
            RETURNING consecutive_low_conf_runs
            """;
        cmd.Parameters.AddWithValue("@id", templateId);
        var result = cmd.ExecuteScalar();
        return result is long l ? (int)l : (result is int i ? i : 0);
    }

    public void ResetTemplateLowConfidenceRuns(string templateId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE workflow_templates SET consecutive_low_conf_runs = 0 WHERE template_id = @id";
        cmd.Parameters.AddWithValue("@id", templateId);
        cmd.ExecuteNonQuery();
    }

    private static WorkflowTemplateRow ReadTemplateRow(SqliteDataReader reader) =>
        new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8), reader.GetDouble(9),
            reader.GetInt32(10), reader.GetInt32(11) == 1,
            reader.GetString(12), reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetInt32(16),
            reader.GetInt32(17) == 1,
            reader.IsDBNull(18) ? null : reader.GetString(18));

    // ── Schema Adaptations + Denylist (v3.12) ──

    public record AppliedSchemaAdaptationRow(
        string AdaptationId, string FromSchemaHash, string ToSchemaHash,
        string RewritesJson, string AppliedAt,
        string? RolledBackAt, string? RollbackReason);

    public void InsertAppliedSchemaAdaptation(string adaptationId, string fromHash,
        string toHash, string rewritesJson, string appliedAt)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO applied_schema_adaptations
                (adaptation_id, from_schema_hash, to_schema_hash, rewrites_json, applied_at)
            VALUES (@id, @from, @to, @rw, @at)
            ON CONFLICT(adaptation_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@id", adaptationId);
        cmd.Parameters.AddWithValue("@from", fromHash);
        cmd.Parameters.AddWithValue("@to", toHash);
        cmd.Parameters.AddWithValue("@rw", rewritesJson);
        cmd.Parameters.AddWithValue("@at", appliedAt);
        cmd.ExecuteNonQuery();
    }

    public AppliedSchemaAdaptationRow? GetAppliedSchemaAdaptation(string adaptationId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT adaptation_id, from_schema_hash, to_schema_hash, rewrites_json,
                   applied_at, rolled_back_at, rollback_reason
            FROM applied_schema_adaptations WHERE adaptation_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", adaptationId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new AppliedSchemaAdaptationRow(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public void RollbackAppliedSchemaAdaptation(string adaptationId, string rolledBackAt, string reason)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            UPDATE applied_schema_adaptations
               SET rolled_back_at = @at, rollback_reason = @reason
             WHERE adaptation_id = @id AND rolled_back_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@id", adaptationId);
        cmd.Parameters.AddWithValue("@at", rolledBackAt);
        cmd.Parameters.AddWithValue("@reason", reason);
        cmd.ExecuteNonQuery();
    }

    public void InsertSchemaAdaptationRevocation(string targetAdaptationId, string revokedAt, string? reason)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_adaptation_denylist (target_adaptation_id, revoked_at, reason)
            VALUES (@id, @at, @r)
            ON CONFLICT(target_adaptation_id) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("@id", targetAdaptationId);
        cmd.Parameters.AddWithValue("@at", revokedAt);
        cmd.Parameters.AddWithValue("@r", (object?)reason ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public bool IsSchemaAdaptationRevoked(string targetAdaptationId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM schema_adaptation_denylist WHERE target_adaptation_id = @id LIMIT 1";
        cmd.Parameters.AddWithValue("@id", targetAdaptationId);
        return cmd.ExecuteScalar() is not null;
    }

    // ── Auto Rule Approvals (v3.12) ──

    public enum AutoRuleStatus { Pending, Shadow, Approved, Rejected }

    public record AutoRuleApprovalRow(
        string RuleId, string TemplateId, string YamlSha256, bool HasWriteback,
        AutoRuleStatus Status,
        int ShadowRuns, int ShadowMatches, int ShadowMismatches,
        string? ApprovedBy, string? ApprovedAt, string? RejectedReason,
        string? ApprovalId = null);

    public void UpsertAutoRuleApproval(string ruleId, string templateId, string yamlSha256,
        AutoRuleStatus status = AutoRuleStatus.Pending,
        bool hasWriteback = false)
    {
        lock (_connLock)
        {
            using var txn = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
            INSERT INTO auto_rule_approvals
                (rule_id, template_id, yaml_sha256, has_writeback, status)
            VALUES
                (@r, @t, @h,
                 COALESCE((SELECT has_writeback FROM workflow_templates WHERE template_id = @t), @hw),
                 @s)
            ON CONFLICT(rule_id) DO UPDATE SET
                template_id = @t,
                yaml_sha256 = @h,
                has_writeback = excluded.has_writeback,
                status = CASE
                    WHEN (auto_rule_approvals.template_id != @t
                          OR auto_rule_approvals.yaml_sha256 != @h
                          OR auto_rule_approvals.has_writeback != excluded.has_writeback)
                     AND auto_rule_approvals.status IN ('Approved','Shadow') THEN 'Pending'
                    ELSE auto_rule_approvals.status
                END,
                approval_id = CASE
                    WHEN (auto_rule_approvals.template_id != @t
                          OR auto_rule_approvals.yaml_sha256 != @h
                          OR auto_rule_approvals.has_writeback != excluded.has_writeback)
                    THEN NULL ELSE auto_rule_approvals.approval_id END,
                approved_by = CASE
                    WHEN (auto_rule_approvals.template_id != @t
                          OR auto_rule_approvals.yaml_sha256 != @h
                          OR auto_rule_approvals.has_writeback != excluded.has_writeback)
                    THEN NULL ELSE auto_rule_approvals.approved_by END,
                approved_at = CASE
                    WHEN (auto_rule_approvals.template_id != @t
                          OR auto_rule_approvals.yaml_sha256 != @h
                          OR auto_rule_approvals.has_writeback != excluded.has_writeback)
                    THEN NULL ELSE auto_rule_approvals.approved_at END,
                rejected_reason = CASE
                    WHEN (auto_rule_approvals.template_id != @t
                          OR auto_rule_approvals.yaml_sha256 != @h
                          OR auto_rule_approvals.has_writeback != excluded.has_writeback)
                    THEN NULL ELSE auto_rule_approvals.rejected_reason END
            """;
            cmd.Parameters.AddWithValue("@r", ruleId);
            cmd.Parameters.AddWithValue("@t", templateId);
            cmd.Parameters.AddWithValue("@h", yamlSha256);
            cmd.Parameters.AddWithValue("@hw", hasWriteback ? 1 : 0);
            cmd.Parameters.AddWithValue("@s", status.ToString());
            cmd.ExecuteNonQuery();

            // A digest, template, or risk change demotes the approval above. Remove the durable
            // runtime admission in the SAME transaction so a stale learned rule cannot remain runnable.
            using var invalidate = _conn.CreateCommand();
            invalidate.Transaction = txn;
            invalidate.CommandText = """
                DELETE FROM active_auto_rule_registry
                 WHERE rule_id = @r
                   AND NOT EXISTS (
                       SELECT 1 FROM auto_rule_approvals a
                        WHERE a.rule_id = @r
                          AND a.status = 'Approved'
                          AND a.template_id = active_auto_rule_registry.template_id
                          AND a.yaml_sha256 = active_auto_rule_registry.yaml_sha256
                          AND a.approval_id = active_auto_rule_registry.approval_id
                   )
                """;
            invalidate.Parameters.AddWithValue("@r", ruleId);
            invalidate.ExecuteNonQuery();
            txn.Commit();
        }
    }

    public AutoRuleApprovalRow? GetAutoRuleApproval(string ruleId)
    {
        lock (_connLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
            SELECT rule_id, template_id, yaml_sha256, has_writeback, status,
                   shadow_runs, shadow_matches, shadow_mismatches,
                   approved_by, approved_at, rejected_reason, approval_id
            FROM auto_rule_approvals WHERE rule_id = @r
            """;
            cmd.Parameters.AddWithValue("@r", ruleId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return ReadAutoRuleApproval(reader);
        }
    }

    /// <summary>
    /// Returns every auto-rule approval row — used by HeartbeatWorker to
    /// upload the current approval state to the cloud mirror. Ordered by
    /// <c>rule_id</c> for deterministic heartbeat payloads (makes upstream
    /// diff detection cheap — same set of rules at rest → identical
    /// payload bytes → no cloud churn).
    /// </summary>
    public IReadOnlyList<AutoRuleApprovalRow> GetAllAutoRuleApprovals()
    {
        lock (_connLock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
            SELECT rule_id, template_id, yaml_sha256, has_writeback, status,
                   shadow_runs, shadow_matches, shadow_mismatches,
                   approved_by, approved_at, rejected_reason, approval_id
            FROM auto_rule_approvals
            ORDER BY rule_id
            """;
            var rows = new List<AutoRuleApprovalRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(ReadAutoRuleApproval(reader));
            return rows;
        }
    }

    private static AutoRuleApprovalRow ReadAutoRuleApproval(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetInt32(3) == 1,
        Enum.Parse<AutoRuleStatus>(reader.GetString(4)),
        reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        reader.IsDBNull(11) ? null : reader.GetString(11));

    /// <summary>
    /// Transitions a local auto-rule approval to a new status in response to a
    /// signed cloud command. The state-machine gate (Pending→Shadow, Shadow→
    /// Approved/Rejected, etc.) is enforced at the cloud API layer — this
    /// call is the agent-side apply, so it trusts the inbound transition and
    /// records the operator metadata.
    ///
    /// Returns true when a row was affected, false when no row existed for the
    /// rule id (silent no-op rather than exception — makes command replays
    /// tolerant of cleaned-up rules).
    /// </summary>
    public bool SetAutoRuleApprovalStatus(
        string ruleId,
        AutoRuleStatus status,
        string? approvedBy = null,
        string? approvedAt = null,
        string? rejectedReason = null)
    {
        lock (_connLock)
        {
            using var txn = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = txn;
            cmd.CommandText = """
            UPDATE auto_rule_approvals
               SET status = @s,
                   approved_by = CASE WHEN @s = 'Approved' THEN @by ELSE NULL END,
                   approved_at = CASE WHEN @s = 'Approved' THEN @at ELSE NULL END,
                   rejected_reason = CASE WHEN @s = 'Rejected' THEN @reason ELSE NULL END
             WHERE rule_id = @r
            """;
            cmd.Parameters.AddWithValue("@r", ruleId);
            cmd.Parameters.AddWithValue("@s", status.ToString());
            cmd.Parameters.AddWithValue("@by", (object?)approvedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@at", (object?)approvedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reason", (object?)rejectedReason ?? DBNull.Value);
            var updated = cmd.ExecuteNonQuery() > 0;
            if (updated && status != AutoRuleStatus.Approved)
            {
                using var remove = _conn.CreateCommand();
                remove.Transaction = txn;
                remove.CommandText =
                    "DELETE FROM active_auto_rule_registry WHERE rule_id = @r";
                remove.Parameters.AddWithValue("@r", ruleId);
                remove.ExecuteNonQuery();
            }
            txn.Commit();
            return updated;
        }
    }

}
