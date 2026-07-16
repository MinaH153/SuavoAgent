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
    private void InitializePricingAndAutonomySchema()
    {
        // Pricing intelligence jobs
        Execute("""
            CREATE TABLE IF NOT EXISTS pricing_jobs (
                job_id TEXT PRIMARY KEY,
                excel_path TEXT NOT NULL,
                ndc_column TEXT NOT NULL DEFAULT 'NDC',
                supplier_column TEXT NOT NULL DEFAULT 'Best Supplier',
                cost_column TEXT NOT NULL DEFAULT 'Best Cost Per Unit',
                cost_basis TEXT NOT NULL DEFAULT 'cost_per_unit',
                status TEXT NOT NULL DEFAULT 'pending',
                total_items INTEGER NOT NULL DEFAULT 0,
                completed_items INTEGER NOT NULL DEFAULT 0,
                failed_items INTEGER NOT NULL DEFAULT 0,
                created_at TEXT DEFAULT (datetime('now')),
                updated_at TEXT DEFAULT (datetime('now'))
            )
        """);
        Execute("""
            CREATE TABLE IF NOT EXISTS pricing_results (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                job_id TEXT NOT NULL,
                row_index INTEGER NOT NULL,
                ndc TEXT NOT NULL,
                found INTEGER NOT NULL DEFAULT 0,
                supplier_name TEXT,
                cost_per_unit REAL,
                package_cost REAL,
                cost_basis TEXT NOT NULL DEFAULT 'cost_per_unit',
                baseline_cost_per_unit REAL,
                quantity REAL,
                error_message TEXT,
                observations_json TEXT,
                created_at TEXT DEFAULT (datetime('now')),
                FOREIGN KEY (job_id) REFERENCES pricing_jobs(job_id)
            )
        """);
        Execute("CREATE INDEX IF NOT EXISTS idx_pricing_results_job ON pricing_results(job_id)");
        // M2a: GREEN-tier selector-resolution telemetry for existing DBs created before the column.
        TryAlter("ALTER TABLE pricing_results ADD COLUMN observations_json TEXT");
        // M1 savings: baseline (today's cost) + aggregate quantity, captured by SQL or Vision, so
        // the cloud can compute (baseline - sourced) * quantity. Nullable — cost-only runs unaffected.
        TryAlter("ALTER TABLE pricing_results ADD COLUMN baseline_cost_per_unit REAL");
        TryAlter("ALTER TABLE pricing_results ADD COLUMN quantity REAL");
        TryAlter("ALTER TABLE pricing_results ADD COLUMN package_cost REAL");
        TryAlter("ALTER TABLE pricing_results ADD COLUMN cost_basis TEXT NOT NULL DEFAULT 'cost_per_unit'");
        TryAlter("ALTER TABLE pricing_jobs ADD COLUMN cost_basis TEXT NOT NULL DEFAULT 'cost_per_unit'");

        // Immutable admission identity for crash-resumable pricing jobs. A
        // job_id can resume only against the same source bytes and the same
        // ordered row/NDC manifest that originally crossed admission.
        Execute("""
            CREATE TABLE IF NOT EXISTS pricing_job_input_identity (
                job_id TEXT PRIMARY KEY,
                source_sha256 TEXT NOT NULL,
                row_fingerprint TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                FOREIGN KEY (job_id) REFERENCES pricing_jobs(job_id)
            )
        """);
        Execute("""
            CREATE TRIGGER IF NOT EXISTS trg_pricing_input_identity_immutable_update
            BEFORE UPDATE ON pricing_job_input_identity
            BEGIN
                SELECT RAISE(ABORT, 'pricing_input_identity_immutable');
            END
        """);
        Execute("""
            CREATE TRIGGER IF NOT EXISTS trg_pricing_input_identity_immutable_delete
            BEFORE DELETE ON pricing_job_input_identity
            BEGIN
                SELECT RAISE(ABORT, 'pricing_input_identity_immutable');
            END
        """);

        Execute("""
            CREATE TABLE IF NOT EXISTS pricing_discovery_candidates (
                token TEXT PRIMARY KEY,
                absolute_path TEXT NOT NULL,
                file_name TEXT,
                created_at TEXT DEFAULT (datetime('now'))
            )
        """);
        Execute("CREATE INDEX IF NOT EXISTS idx_pricing_discovery_candidates_created ON pricing_discovery_candidates(created_at)");

        // M3 per-task autonomy graduation ledger — how far a (task, pharmacy) has EARNED up the FSD
        // autonomy ladder via consecutive clean verified runs. Capability only; unsupervised
        // execution is gated separately + fail-closed.
        Execute("""
            CREATE TABLE IF NOT EXISTS task_autonomy (
                task_key TEXT NOT NULL,
                pharmacy_id TEXT NOT NULL,
                consecutive_clean INTEGER NOT NULL DEFAULT 0,
                total_runs INTEGER NOT NULL DEFAULT 0,
                last_outcome TEXT,
                updated_at TEXT DEFAULT (datetime('now')),
                PRIMARY KEY (task_key, pharmacy_id)
            )
        """);

        // v3.12 — numbered transactional migrations (Codex Area 5 fix).
        // schema_migrations tracks applied versions so new migrations can fail-closed.
        // Existing TryAlter migrations are left intact for backward compatibility.
        Execute("""
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                description TEXT NOT NULL
            )
        """);
    }
}
