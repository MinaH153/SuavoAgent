namespace SuavoAgent.Core.State;

public sealed partial class AgentStateDb
{
    private void ApplyPricingCandidatePrivacyMigration()
    {
        ApplyMigrationIfNeeded(25,
            "purge ephemeral pricing candidates and remove filename metadata",
            """
            DROP INDEX IF EXISTS idx_pricing_discovery_candidates_created;
            DROP TABLE IF EXISTS pricing_discovery_candidates;
            CREATE TABLE pricing_discovery_candidates (
                token TEXT PRIMARY KEY,
                absolute_path TEXT NOT NULL,
                created_at TEXT DEFAULT (datetime('now'))
            );
            CREATE INDEX idx_pricing_discovery_candidates_created
                ON pricing_discovery_candidates(created_at);
            """);
    }
}
