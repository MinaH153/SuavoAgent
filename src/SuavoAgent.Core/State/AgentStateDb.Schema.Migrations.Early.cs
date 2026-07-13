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
    private void ApplyEarlyVersionedMigrations()
    {
        ApplyMigrationIfNeeded(1,
            "v3.12 workflow templates + auto-rule approvals + schema adaptation denylist",
            """
            CREATE TABLE IF NOT EXISTS workflow_templates (
                template_id TEXT PRIMARY KEY,
                template_version TEXT NOT NULL,
                skill_id TEXT NOT NULL,
                process_name_glob TEXT NOT NULL,
                pms_version_range_json TEXT NOT NULL,
                screen_signature TEXT NOT NULL,
                steps_hash TEXT NOT NULL,
                routine_hash_origin TEXT,
                steps_json TEXT NOT NULL,
                aggregate_confidence REAL NOT NULL,
                observation_count INTEGER NOT NULL,
                has_writeback INTEGER NOT NULL,
                extracted_at TEXT NOT NULL,
                extracted_by TEXT NOT NULL,
                retired_at TEXT,
                retirement_reason TEXT,
                consecutive_low_conf_runs INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_wt_skill ON workflow_templates(skill_id) WHERE retired_at IS NULL;
            CREATE INDEX IF NOT EXISTS idx_wt_writeback ON workflow_templates(has_writeback)
                WHERE retired_at IS NULL AND has_writeback = 1;
            -- Partial unique index: only ONE active template per (skill, screen). Retired
            -- rows are exempt so version bumps with same screen_signature can coexist.
            CREATE UNIQUE INDEX IF NOT EXISTS uniq_wt_active_skill_screen
                ON workflow_templates(skill_id, screen_signature) WHERE retired_at IS NULL;

            CREATE TABLE IF NOT EXISTS auto_rule_approvals (
                rule_id TEXT PRIMARY KEY,
                template_id TEXT NOT NULL,
                yaml_sha256 TEXT NOT NULL,
                status TEXT NOT NULL,
                shadow_runs INTEGER NOT NULL DEFAULT 0,
                shadow_matches INTEGER NOT NULL DEFAULT 0,
                shadow_mismatches INTEGER NOT NULL DEFAULT 0,
                approved_by TEXT,
                approved_at TEXT,
                rejected_reason TEXT
            );

            CREATE TABLE IF NOT EXISTS schema_adaptation_denylist (
                target_adaptation_id TEXT PRIMARY KEY,
                revoked_at TEXT NOT NULL,
                reason TEXT
            );
            """);
        ApplyMigrationIfNeeded(2,
            "v3.12 applied schema adaptations (track what each pharmacy has installed)",
            """
            CREATE TABLE IF NOT EXISTS applied_schema_adaptations (
                adaptation_id TEXT PRIMARY KEY,
                from_schema_hash TEXT NOT NULL,
                to_schema_hash TEXT NOT NULL,
                rewrites_json TEXT NOT NULL,
                applied_at TEXT NOT NULL,
                rolled_back_at TEXT,
                rollback_reason TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_asa_from ON applied_schema_adaptations(from_schema_hash);
            """);
        // Migration 3 (fix 2026-06-03): the M2b selector-patch store was originally added to the BODY
        // of already-applied migration #1, so every box that applied #1 in the v3.12 era never created
        // the table — UpsertSelectorPatch then threw `no such table: selector_patches`, making BOTH the
        // operator update_selector correction AND the fleet seed-apply dead-on-arrival on every upgraded
        // box (field-confirmed on Mina's box, 2026-06-03). Moved into its own versioned migration so
        // existing DBs create it on next startup. Fresh DBs get it here too (#1 no longer defines it).
        ApplyMigrationIfNeeded(3,
            "M2b learned selector-patch correction store (fix: was wrongly bolted onto migration 1)",
            """
            CREATE TABLE IF NOT EXISTS selector_patches (
                patch_id TEXT PRIMARY KEY,
                skill_id TEXT NOT NULL,
                step_id TEXT NOT NULL,
                pms_fingerprint TEXT,
                screen_signature TEXT,
                target_json TEXT NOT NULL,
                fallbacks_json TEXT NOT NULL,
                confidence REAL NOT NULL,
                seed_digest TEXT NOT NULL,
                version INTEGER NOT NULL,
                applied_at TEXT NOT NULL,
                retired_at TEXT,
                retirement_reason TEXT,
                consecutive_failures INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_sp_skill_step
                ON selector_patches(skill_id, step_id) WHERE retired_at IS NULL;
            """);
        // Migration 4 — Physarum edge-conductance store (the moat's slime-mold exploration memory).
        // One row per (pharmacy, task, screen-state, action) frontier edge; conductance is the
        // externalized "tube thickness" reinforced ONLY on a verified Met (EdgeReinforcement) and
        // decayed otherwise. State lives OUTSIDE the model — this table IS that state. PHI-safe:
        // state_hash + action_sig are scrubbed structural strings, never patient content.
        ApplyMigrationIfNeeded(4,
            "Physarum edge-conductance store (verified-only exploration memory)",
            """
            CREATE TABLE IF NOT EXISTS edge_conductance (
                pharmacy_id TEXT NOT NULL,
                task_key TEXT NOT NULL,
                state_hash TEXT NOT NULL,
                action_sig TEXT NOT NULL,
                conductance REAL NOT NULL,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (pharmacy_id, task_key, state_hash, action_sig)
            );
            CREATE INDEX IF NOT EXISTS idx_edge_conductance_task
                ON edge_conductance(pharmacy_id, task_key);
            """);
        // Migration 5 — verified-skill store (the amortize ratchet, lever 3). One row per banked
        // (pharmacy, task, app, verified-path); success_count = how many times the SAME verified path was
        // re-confirmed (the tube thickening). Steps are scrubbed structural (state hash + action signature)
        // — PHI-safe. Distinct from workflow_templates (the legacy DFG path); this is the explorer's own
        // vocabulary so a label/signature-click trajectory banks faithfully.
        ApplyMigrationIfNeeded(5,
            "Verified-skill store (amortize ratchet — derive once, bank, replay)",
            """
            CREATE TABLE IF NOT EXISTS verified_skills (
                skill_id TEXT PRIMARY KEY,
                pharmacy_id TEXT NOT NULL,
                task_key TEXT NOT NULL,
                app TEXT NOT NULL,
                steps_json TEXT NOT NULL,
                steps_hash TEXT NOT NULL,
                success_count INTEGER NOT NULL DEFAULT 0,
                first_verified_at TEXT NOT NULL,
                last_verified_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_verified_skills_task
                ON verified_skills(pharmacy_id, task_key);
            """);
        // Migration 6 — skill decay/retirement (slime-mold cache hygiene for skills, mirroring the
        // edge-conductance evaporation). A banked skill that keeps FAILING replay (UI drift, stale path)
        // accrues consecutive failures and retires, so it stops being auto-selected and exploration
        // re-learns the task. A successful replay resets the failure streak (the tube re-thickens).
        ApplyMigrationIfNeeded(6,
            "Verified-skill decay/retirement (replay-outcome feedback)",
            """
            ALTER TABLE verified_skills ADD COLUMN failure_count INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE verified_skills ADD COLUMN consecutive_failures INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE verified_skills ADD COLUMN retired_at TEXT;
            ALTER TABLE verified_skills ADD COLUMN retirement_reason TEXT;
            """);
    }
}
