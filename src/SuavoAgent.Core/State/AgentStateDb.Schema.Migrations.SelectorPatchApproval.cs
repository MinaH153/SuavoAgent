namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplySelectorPatchApprovalMigration()
    {
        ApplyMigrationIfNeeded(28,
            "Bind learned selector actuation to explicit pharmacist role approval",
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
            ALTER TABLE selector_patches
                ADD COLUMN approved_by_role TEXT;
            """);
    }
}
