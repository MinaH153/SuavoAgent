namespace SuavoAgent.Core.Config;

public sealed class AgentOptions
{
    public string CloudUrl { get; set; } = "https://suavollc.com";
    public string? ApiKey { get; set; }
    public string? AgentId { get; set; }
    public string? PharmacyId { get; set; }
    public string? MachineFingerprint { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int HeartbeatJitterSeconds { get; set; } = 5;
    public string? WatchdogRepairRequestPath { get; set; }
    public string Version { get; set; } = "3.9.2";
    public string UpdateChannel { get; set; } = "stable";
    public string? SqlServer { get; set; }
    public string? SqlDatabase { get; set; }
    public string? SqlUser { get; set; }
    public string? SqlPassword { get; set; }

    /// <summary>
    /// When true, SQL connections accept any server certificate. This is an explicit
    /// break-glass compatibility override for pharmacies with self-signed SQL Server
    /// certificates; default false avoids silent LAN MITM exposure.
    /// </summary>
    public bool SqlTrustServerCertificate { get; set; } = false;

    /// <summary>
    /// Per-agent HMAC salt for hashing PHI (Rx numbers, etc.) in audit logs and cloud sync.
    /// Auto-generated on first use, persisted in state.db. NOT the AgentId (which is public).
    /// Set by Program.cs after DB initialization — do not set in appsettings.json.
    /// </summary>
    public string? HmacSalt { get; set; }

    /// <summary>
    /// Base64 SHA-256 of cloud server public key for cert pinning. Multiple pins separated by semicolons. Empty = OS cert store only.
    /// </summary>
    public string? CloudCertPin { get; set; }

    /// <summary>
    /// Maximum prescriptions per detection query. Default 100. Increase for high-volume pharmacies.
    /// </summary>
    public int MaxDetectionBatchSize { get; set; } = 100;

    /// <summary>
    /// Opt-in gate for the legacy <c>rxDeliveryQueue</c> shape on the sync
    /// payload. Default false. Track 2 field proof uses
    /// <c>rxOrderCandidates</c> exclusively; this shape exists only for
    /// legacy cloud routes that haven't migrated yet.
    /// <para>
    /// Track 3 invariant (Codex CRITICAL #15, closed 2026-05-12): even when
    /// this is <c>true</c>, the queue ships ONLY operational metadata —
    /// hashed Rx number, drug name, NDC, fill date, quantity, status GUID,
    /// detection timestamp. Patient name / phone / address are intentionally
    /// excluded by <c>RxDetectionWorker.SerializeRxBatch</c>; they flow
    /// exclusively through the typed signed-command path
    /// <c>SuavoCloudClient.SendPatientDetailsAsync</c>.
    /// </para>
    /// </summary>
    public bool EnableLegacyPhiDeliveryQueueSync { get; set; } = false;

    /// <summary>
    /// Fail-closed gate for outbound patient-detail PHI egress
    /// (<c>SuavoCloudClient.SendPatientDetailsAsync</c> →
    /// <c>/api/agent/patient-details</c>). Default <c>false</c>.
    /// <para>
    /// Closed 2026-06-04 (precedence-1): the cloud
    /// <c>/api/agent/patient-details</c> route does NOT exist in the canonical
    /// tree and <c>OutboundPhiGuard</c> unconditionally exempts the path, so an
    /// enabled egress shipped unscrubbed PHI (driver name/address) over the wire
    /// to a 404 — leaking into edge/proxy logs. Until the audited route + typed
    /// positive-allowlist contract + <c>phi_egress_audit</c> land (plan Stage A1),
    /// this stays <c>false</c> and the agent never POSTs patient PHI.
    /// </para>
    /// </summary>
    public bool EnableAuditedPatientDetailsEgress { get; set; } = false;

    /// <summary>
    /// Switches the outbound PHI guard's value check from a deny-list to a positive
    /// token allow-list. Default <c>false</c> (SHADOW MODE): behavior is unchanged —
    /// the guard logs what strict mode WOULD block (field name only, never the value)
    /// so false-positives can be measured on a real pilot before enforcement. When
    /// <c>true</c> (STRICT): any outbound string that is not on the operational token
    /// allow-list (hash/uuid/ndc/iso/semver/enum…) — and any geographic field
    /// (city/state/zip5) — is BLOCKED before the POST, closing the
    /// <c>&lt;=96-char safe-charset</c> escape hatch that lets a packed identifier
    /// (e.g. <c>DOE-JOHN-1990</c>) egress unscanned.
    /// <para>
    /// Flip to <c>true</c> ONLY after the shadow would-block logs show no legitimate
    /// telemetry is caught (the #105/#106/#107 false-positive lesson — a too-strict
    /// allow-list silently takes the agent offline on a live PMS).
    /// </para>
    /// </summary>
    public bool StrictOutboundTokenAllowlist { get; set; } = false;

    /// <summary>
    /// When true, agent runs in learning mode (30-day observation).
    /// When false, uses the existing PioneerRx adapter directly.
    /// </summary>
    public bool LearningMode { get; set; }

    /// <summary>
    /// Default: true. Agent generates digital delivery receipts (signature, photo, timestamp)
    /// replacing the paper receipt + scanner workflow. Receipts are DPAPI-encrypted locally
    /// and viewable on the pharmacy dashboard.
    /// When false, agent ALSO writes delivery status back to PMS SQL (requires explicit opt-in).
    /// </summary>
    public bool ReceiptOnlyMode { get; set; } = true;

    /// <summary>
    /// Retention period for delivery receipt files in days. Default 2555 (7 years).
    /// Covers most conservative state pharmacy record retention requirements.
    /// DEA minimum for controlled substance records is 730 days (2 years).
    /// </summary>
    public int ReceiptRetentionDays { get; set; } = 2555;

    /// <summary>
    /// Tier-2 (LocalInference) configuration. When Enabled=false or ModelPath
    /// not present on disk, the agent runs rules-only and TieredBrain escalates
    /// any NoMatch straight to the operator approval queue.
    /// </summary>
    public ReasoningOptions Reasoning { get; set; } = new();

    /// <summary>
    /// Autonomous workflow template extraction. Enabled=false by default; pilot
    /// pharmacies opt in. Enabled capture mode records encrypted observations
    /// and counters only. Rule emission requires TemplateLearning.RuleGeneration
    /// plus a non-capture mode.
    /// </summary>
    public TemplateLearningOptions TemplateLearning { get; set; } = new();

    /// <summary>
    /// Global execution brake. Defaults to observe-only: no autonomous actions,
    /// confirmations required, and no PMS writeback.
    /// </summary>
    public AutoExecutionOptions AutoExecution { get; set; } = new();

    /// <summary>
    /// v3.12 — Fleet Schema Canary propagation. Enabled=false by default
    /// (contract-only in v3.12; cloud endpoint lands v3.12.1).
    /// </summary>
    public FleetFeaturesOptions FleetFeatures { get; set; } = new();

    /// <summary>
    /// Fleet-learning rollout. Default OFF: the agent pulls fleet seeds but does
    /// NOT apply the consensus rx_queue_shape warm-start until a pilot opts in.
    /// </summary>
    public FleetLearningOptions FleetLearning { get; set; } = new();

    /// <summary>
    /// Vision pipeline configuration (screenshot capture + extraction). Off by
    /// default — enabling adds a new HIPAA surface (encrypted screens on disk).
    /// </summary>
    public VisionOptions Vision { get; set; } = new();

    /// <summary>
    /// Self-healing knobs. WorkerSupervisor restarts a faulted worker in-process instead of
    /// letting it die silently while the service still shows "Running". Default on; kill-switch
    /// via config-sync if a worker ever restart-loops.
    /// </summary>
    public SelfHealOptions SelfHeal { get; set; } = new();

    /// <summary>
    /// Test-only hooks. Default OFF. When enabled (signed config-override
    /// <c>Agent.TestHooks.Enabled=true</c>), unlocks deterministic test seams such as the
    /// <c>force_learning_phase</c> signed command used to exercise the M1 PhaseGate on a real
    /// box. These seams never bypass a safety gate — they only drive single-step phase
    /// transitions that the gate then evaluates as normal. Must stay false in the field.
    /// </summary>
    public TestHooksOptions TestHooks { get; set; } = new();

    /// <summary>
    /// Multi-pharmacy config. When populated, each entry gets its own detection worker.
    /// Backwards-compatible: if empty, falls back to the top-level SqlServer/PharmacyId fields.
    /// </summary>
    public List<PharmacyConfig> Pharmacies { get; set; } = new();

    /// <summary>
    /// Which IPricingJobExecutor implementation runs <c>run_pricing_job</c> / <c>find_and_run_pricing_job</c>.
    ///
    /// <c>SqlFirst</c> (default): SqlFirstPricingJobExecutor reads pricing from the PioneerRx SQL backend
    /// directly. Fast (~30s for 500 NDCs) and fail-closed — no UIA fallback if SQL is unavailable.
    ///
    /// <c>UiaFirst</c>: UiaFirstPricingJobExecutor drives PioneerRx through its UI (Item → Rx Item →
    /// Quick Search → Pricing tab) for every NDC. Slower (minutes to ~half hour for 500 NDCs depending
    /// on throttle) but stays inside the documented operator workflow — no direct DB connection,
    /// no vendor-EULA tamper question. Use for pharmacies that have not (yet) authorized SQL access.
    /// </summary>
    public PricingExecutorMode PricingExecutor { get; set; } = PricingExecutorMode.SqlFirst;

    /// <summary>
    /// M1 savings: when true, a pricing run enriches each found cheapest-cost result with the
    /// pharmacy's baseline cost + dispensed quantity so the cloud can compute a dollar savings.
    /// OFF by default (fail-closed): the figure must be verified against the live box before it is
    /// trusted, so an unverified savings number is never emitted in production until this is enabled.
    /// </summary>
    public bool EnablePricingSavingsEnrichment { get; set; } = false;

    /// <summary>
    /// Trailing window (days) over which dispensed quantity is aggregated for the savings figure.
    /// Default 90 — a realistic recurring volume per NDC.
    /// </summary>
    public int PricingSavingsWindowDays { get; set; } = 90;

    /// <summary>
    /// Header (case-insensitive partial match) of the workbook column carrying the pharmacy's OWN
    /// current cost per NDC. When set and present, this is the baseline (most honest — what he says
    /// he pays — and needs no PMS/Vision). Null = don't read it; baseline comes from the provider.
    /// </summary>
    public string? PricingBaselineCostColumn { get; set; }

    /// <summary>
    /// Header (case-insensitive partial match) of the workbook column carrying dispensed quantity
    /// per NDC. When set and present, used instead of the SQL volume read. Null = don't read it.
    /// </summary>
    public string? PricingQuantityColumn { get; set; }

    /// <summary>
    /// RxTransaction status descriptions that count as a real dispense for the SQL volume SUM
    /// (e.g. "Sold", "Dispensed", "Completed"). MUST be ground-truthed against the live PioneerRx
    /// box — empty means the SQL volume read is disabled (fail-safe: never count voids/reversals).
    /// Only the workbook quantity path works until this is configured.
    /// </summary>
    public List<string> PricingDispensedStatusNames { get; set; } = new();

    /// <summary>
    /// Delay between successive NDC lookups within a single pricing job, in milliseconds.
    /// Throttle exists to (a) avoid hammering PioneerRx during a 500-row batch and (b) stay below
    /// any anti-automation heuristic the vendor may apply. Default <c>1500</c> ms keeps a 500-NDC
    /// run at ~12.5 min — comfortably human-paced under UIA mode. SQL-first mode can safely lower
    /// this since DB lookups don't drive the operator's keyboard.
    /// Range: clamped to [0, 30000] ms at runner construction.
    /// </summary>
    public int PricingThrottleMs { get; set; } = 1500;

    /// <summary>
    /// Returns the effective pharmacy list — either the explicit Pharmacies array
    /// or a single entry synthesized from top-level fields.
    /// </summary>
    public IReadOnlyList<PharmacyConfig> GetEffectivePharmacies()
    {
        if (Pharmacies.Count > 0) return Pharmacies;
        if (string.IsNullOrEmpty(SqlServer)) return Array.Empty<PharmacyConfig>();
        return new[]
        {
            new PharmacyConfig
            {
                PharmacyId = PharmacyId ?? "",
                SqlServer = SqlServer,
                SqlDatabase = SqlDatabase,
                SqlUser = SqlUser,
                SqlPassword = SqlPassword,
            }
        };
    }
}

public enum PricingExecutorMode
{
    /// <summary>SQL-first, fail-closed (default). See <c>AgentOptions.PricingExecutor</c>.</summary>
    SqlFirst,
    /// <summary>UIA-first via Helper. No SQL fallback. See <c>AgentOptions.PricingExecutor</c>.</summary>
    UiaFirst,
}

public sealed class SelfHealOptions
{
    /// <summary>
    /// Restart a faulted <c>ResilientHostedService</c> worker in-process (bounded backoff) instead
    /// of letting it die silently. Default true; set false as a kill-switch (faults then propagate
    /// as before). Gates the workers that read AgentOptions (RxDetection, Writeback, Heartbeat);
    /// ConfigSync is a low-risk config-poll loop and supervises unconditionally.
    /// </summary>
    public bool WorkerSupervisorEnabled { get; set; } = true;
}

public sealed class PharmacyConfig
{
    public string PharmacyId { get; set; } = "";
    public string SqlServer { get; set; } = "";

    /// <summary>PMS family this pharmacy runs (registry key). Defaults to PioneerRx for back-compat.</summary>
    public string AdapterType { get; set; } = "pioneerrx";

    /// <summary>
    /// Explicit catalog override. Null = fall back to the resolved adapter's default catalog
    /// (see <see cref="SuavoAgent.Core.Adapters.AdapterCatalog"/>); no longer hardcodes PioneerRx.
    /// </summary>
    public string? SqlDatabase { get; set; }
    public string? SqlUser { get; set; }
    public string? SqlPassword { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class AutoExecutionOptions
{
    public bool Enabled { get; set; } = false;
    public bool RequireConfirmation { get; set; } = true;
    public bool WritebackEnabled { get; set; } = false;
}
