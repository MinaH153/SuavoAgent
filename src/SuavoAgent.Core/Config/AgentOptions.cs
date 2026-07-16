using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Core.Config;

public sealed class AgentOptions
{
    public string CloudUrl { get; set; } = "https://suavollc.com";
    public string? ApiKey { get; set; }
    public string? AgentId { get; set; }
    public string? PharmacyId { get; set; }
    public string? MachineFingerprint { get; set; }
    /// <summary>
    /// Exact versioned TPM key enrolled for this install. During install
    /// probation it may still be the local pending slot; after health cutover
    /// the same key becomes the active slot without changing this identity.
    /// </summary>
    public string? DeviceAttestationKeyName { get; set; }
    public string? DeviceAttestationKeyId { get; set; }
    /// <summary>Public ID of the SYSTEM-only TPM maintenance authority. Core cannot sign with it.</summary>
    public string? MaintenanceAttestationKeyId { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int HeartbeatJitterSeconds { get; set; } = 5;
    public string? WatchdogRepairRequestPath { get; set; }
    /// <summary>
    /// Test/deployment override for the authenticated Core-to-Broker self-uninstall handoff.
    /// Production leaves this unset and uses ProgramData\SuavoAgent\uninstall.request.
    /// </summary>
    public string? SelfUninstallRequestPath { get; set; }
    public string Version { get; set; } = "3.9.2";
    /// <summary>
    /// Runtime-only nonce for a staged install credential. It is loaded from
    /// the DPAPI store, never from appsettings or cloud config, and binds local
    /// readiness evidence to this exact install probation transaction.
    /// </summary>
    public string? InstallProvisioningId { get; set; }
    /// <summary>Runtime-only pending pairing proof fields; never appsettings.</summary>
    public string? InstallDeviceCode { get; set; }
    public string? InstallDeviceChallenge { get; set; }
    public string UpdateChannel { get; set; } = "stable";
    public string? SqlServer { get; set; }
    public string? SqlDatabase { get; set; }
    public string? SqlUser { get; set; }
    public string? SqlPassword { get; set; }

    /// <summary>
    /// Legacy compatibility setting. Production SQL activation rejects <c>true</c>: encryption must
    /// validate the server certificate chain and hostname. A schema fingerprint is not authentication.
    /// </summary>
    public bool SqlTrustServerCertificate { get; set; } = false;

    /// <summary>
    /// Lowercase SHA-256 of the exact SQL Server public certificate RawData. For self-signed
    /// PioneerRx deployments this value must also be bound into the active, dual-signed local
    /// PioneerRx approval receipt. Remote config cannot change it.
    /// </summary>
    public string? SqlServerCertificateSha256 { get; set; }

    /// <summary>
    /// Runtime-only fixed ProgramData path after certificate bytes, ACLs, workstation approval,
    /// authority counter, and digest have all been verified. Configuration cannot set this field.
    /// </summary>
    internal string? ValidatedSqlServerCertificatePath { get; set; }

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
    /// Retained only so old signed configuration documents still deserialize. The legacy
    /// <c>rxDeliveryQueue</c> wire shape has been permanently removed: setting this value cannot
    /// enable it, and <c>/api/agent/sync</c> never receives a PHI exemption.
    /// </summary>
    public bool EnableLegacyPhiDeliveryQueueSync { get; set; } = false;

    /// <summary>
    /// Fail-closed gate for outbound patient-detail PHI egress
    /// (<c>SuavoCloudClient.SendApprovedPatientDetailsAsync</c> →
    /// <c>/api/agent/patient-details</c>). Default <c>false</c>.
    /// <para>
    /// The only caller is the typed, signed-command-driven closed loop. Generic
    /// <c>PostSignedAsync</c> calls receive no path exemption and cannot use this flag to
    /// bypass the outbound PHI guard. Enable only after the audited cloud route and
    /// staging migration are deployed together.
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
    /// <c>UiaFirst</c> (default): UiaFirstPricingJobExecutor drives PioneerRx through its UI (Item →
    /// Rx Item → Quick Search → Pricing tab) for every NDC. Slower (minutes to ~half hour for 500 NDCs
    /// depending on throttle) but stays inside the documented operator workflow — no direct DB
    /// connection, no SQL credentials to provision, no vendor-EULA tamper question, and INVISIBLE to
    /// the PMS (the agent just "uses" PioneerRx like a pharmacist). This is the default because stealth
    /// is the product model: a pharmacy install must not have to expose the agent to PioneerRx/the
    /// vendor (asking a DBA for a SQL login does exactly that). Requires PioneerRx to be open.
    ///
    /// <c>SqlFirst</c>: SqlFirstPricingJobExecutor reads pricing from the PioneerRx SQL backend directly.
    /// Faster (~30s for 500 NDCs) and runs headless, but requires provisioned SQL access. Opt in per
    /// pharmacy via the Agent.PricingExecutor cloud override once the pharmacy has authorized SQL access.
    /// </summary>
    public PricingExecutorMode PricingExecutor { get; set; } = PricingExecutorMode.UiaFirst;

    /// <summary>
    /// Explicit tenant pharmacist-in-charge approval for the exact pricing policy digest. Default
    /// denied. A schema, status policy, modality, cost-basis, tenant, role, or expiry change blocks
    /// pricing until a new signed configuration approval is installed.
    /// </summary>
    public PricingCostBasisApprovalOptions PricingCostBasisApproval { get; set; } = new();

    /// <summary>
    /// M3 autonomy: master enable for UNSUPERVISED execution of EARNED tasks. OFF by default
    /// (fail-closed) — a task that has graduated to Eligible still does not run unattended until
    /// this is explicitly turned on for the deployment. The ledger only ever raises capability; this
    /// flag is the human flipping the switch.
    /// </summary>
    public bool EnableTaskAutonomy { get; set; } = false;

    /// <summary>
    /// M3 autonomy: consecutive clean verified runs a (task, pharmacy) must earn before it becomes
    /// Eligible for unsupervised execution. Default 12 (mirrors the "12/12 clean → eligible" gate).
    /// </summary>
    public int TaskAutonomyCleanRunsThreshold { get; set; } = 12;

    /// <summary>
    /// M1 savings: when true, a pricing run enriches each found cheapest-cost result with the
    /// pharmacy's baseline cost + dispensed quantity so the cloud can compute a dollar savings.
    /// OFF by default (fail-closed): the figure must be verified against the live box before it is
    /// trusted, so an unverified savings number is never emitted in production until this is enabled.
    /// </summary>
    public bool EnablePricingSavingsEnrichment { get; set; } = false;

    /// <summary>
    /// MOAT Increment 2 — replay-first (<c>Agent:ReplayFirst</c>). When true, a navigate_app /
    /// explore_sandbox objective whose (pharmacy, taskKey, app) has a HEALTHY banked
    /// <c>VerifiedSkill</c> (success_count ≥ 2, click-family steps only) whose first step's StateHash
    /// matches the live screen is satisfied by deterministic <c>VerifiedSkillReplayer</c> replay —
    /// ZERO LLM/cloud reasoning. Any miss/drift falls through to the agentic loop unchanged.
    /// OFF by default (fail-closed); flipped per-box only after sandbox validation. Layered safety
    /// (keep all four): this flag default-off + click-only-v1 + StateHash entry match + the SAME
    /// composite navigate gate (preflight / autonomy / never-blind-on-live-PMS) as reasoned actions.
    /// NOT the unrelated <see cref="LearningMode"/> 30-day-observation flag.
    /// </summary>
    public bool ReplayFirst { get; set; } = false;

    /// <summary>
    /// MOAT Increment 2 follow-up (<c>Agent:ReplayFirstAllowTypeSteps</c>). v1 HARD RULE: a skill
    /// containing <c>type_into_field</c>/<c>press_keys</c> steps must NOT auto-replay — a banked
    /// literal types the OLD value, and the "screen changed" postcondition would certify a
    /// semantically wrong write. Such skills replay only via the explicit operator
    /// <c>replay_skill</c> command. This flag pre-declares the future opt-in for when parameterized
    /// skills + Helper focused-target binding for type/press exist. MUST stay false in v1.
    /// </summary>
    public bool ReplayFirstAllowTypeSteps { get; set; } = false;

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

    /// <summary>Plausibility guard: reject a baseline per-unit cost at/above this (a data/column/unit
    /// error). Generous — specialty drugs are costly; this only catches magnitude blunders.</summary>
    public decimal PricingMaxPlausibleUnitCost { get; set; } = 1_000_000m;

    /// <summary>Plausibility guard: reject an aggregate quantity at/above this (a data error).</summary>
    public decimal PricingMaxPlausibleQuantity { get; set; } = 100_000_000m;

    /// <summary>Plausibility guard: FLAG (don't suppress) a savings whose fraction of baseline is
    /// at/above this for human review — a real generic switch can save this much, so a human
    /// verifies the column/units rather than the agent hiding a genuine win. Default 90%.</summary>
    public decimal PricingSuspiciousSavingsFraction { get; set; } = 0.9m;

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
    /// Autonomous pricing schedule — the "the bot does it on its own" loop Nadim asked for (the
    /// documented overnight 500-row batch). When enabled, the agent runs the configured workbook
    /// through the pricing pipeline on a daily schedule WITHOUT a cockpit command. OFF by default.
    /// Deliberately SqlFirst-only at the worker (read-only SQL + Excel writeback, no PMS UI
    /// actuation) — autonomous keystroke-driving of a live PMS stays operator-triggered (Precedence-1).
    /// </summary>
    public PricingScheduleOptions PricingSchedule { get; set; } = new();

    /// <summary>
    /// Top-dispensed worklist generation — the report Nadim otherwise makes by hand (Rx Binoculars →
    /// Transaction Search → Top-X → export). When wired, the agent runs the SQL top-N-dispensed query,
    /// writes the Drug/Strength/NDC/Total sheet, and feeds it to the pricing loop. Column names +
    /// generic/Rx/schedule VALUES are pharmacy-specific and confirmed on the live PMS; leave the
    /// overrides null to let <c>TopDispensedSchemaResolver</c> discover them from the Item schema.
    /// </summary>
    public TopDispensedOptions TopDispensed { get; set; } = new();

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

public sealed class PricingCostBasisApprovalOptions
{
    /// <summary>Legacy field retained only for strict backwards deserialization. It grants no authority.</summary>
    public bool Approved { get; set; } = false;
    public int SchemaVersion { get; set; }
    public string ApprovalId { get; set; } = "";
    public string PharmacyId { get; set; } = "";
    public string ApproverId { get; set; } = "";
    public string ApprovedByRole { get; set; } = "";
    public string CostBasis { get; set; } = "";
    public string PolicyDigest { get; set; } = "";
    public DateTimeOffset IssuedAtUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;
    public string KeyId { get; set; } = "";
    public string Signature { get; set; } = "";
}

public enum PricingExecutorMode
{
    /// <summary>SQL-first, fail-closed (default). See <c>AgentOptions.PricingExecutor</c>.</summary>
    SqlFirst,
    /// <summary>UIA-first via Helper. No SQL fallback. See <c>AgentOptions.PricingExecutor</c>.</summary>
    UiaFirst,
    /// <summary>Vision-first via Helper: reads the Pricing grid BY SIGHT (screen capture + on-device
    /// OCR) with a UIA exact-verify so a misread never writes a wrong cost. Drives the screen exactly
    /// like <see cref="UiaFirst"/>; the sighted read activates when protected registry state enables it on the box.</summary>
    VisionFirst,
}

/// <summary>
/// Config for generating the top-N-most-dispensed worklist (<see cref="AgentOptions.TopDispensed"/>).
/// Mirrors Nadim's Rx Binoculars recipe: generics only, Rx-not-OTC, no schedule, over a window,
/// ranked by dispensed quantity. Column names default to heuristic discovery
/// (<c>TopDispensedSchemaResolver</c>); the *Value fields say what "generic"/"Rx"/"no schedule"
/// equal in this install's data — confirmed on the live PMS.
/// </summary>
public sealed class TopDispensedOptions
{
    /// <summary>How many top items to emit (Nadim: 500). Default 500.</summary>
    public int TopN { get; set; } = 500;

    /// <summary>Look-back window in days for "dispensed since" (Nadim uses Jan 1 → today; a
    /// window keeps it rolling). Default 180.</summary>
    public int WindowDays { get; set; } = 180;

    /// <summary>
    /// RxTransaction status descriptions that count as a real dispense. REQUIRED to generate —
    /// an empty list fails closed (never counts voids/reversals). Ground-truthed on the box; shared
    /// intent with <see cref="AgentOptions.PricingDispensedStatusNames"/>.
    /// </summary>
    public List<string> DispensedStatusNames { get; set; } = new();

    // --- Optional Item-column pins (null → discover) + the match values for this install ---
    public string? DrugNameColumn { get; set; }
    public string? StrengthColumn { get; set; }
    public string? BrandGenericColumn { get; set; }
    public string? RxOtcColumn { get; set; }
    public string? ScheduleColumn { get; set; }

    /// <summary>What the Brand/Generic column equals for a generic (default "Generic").</summary>
    public string GenericValue { get; set; } = "Generic";
    /// <summary>What the Rx/OTC column equals for a legend (Rx) drug (default "Rx").</summary>
    public string RxValue { get; set; } = "Rx";
    /// <summary>What the schedule column equals for "no controlled schedule" (default "0").</summary>
    public string NoScheduleValue { get; set; } = "0";
}

/// <summary>
/// Autonomous pricing schedule (<see cref="AgentOptions.PricingSchedule"/>). When <see cref="Enabled"/>
/// and a <see cref="WorkbookPath"/> is set, <c>PricingScheduleWorker</c> runs the pricing pipeline on
/// the configured workbook once per day at <see cref="RunAtLocalTime"/>, with no cockpit command. OFF
/// by default. The worker only runs when <c>PricingExecutor == SqlFirst</c> (read-only); it refuses to
/// autonomously drive the PMS UI.
/// </summary>
public sealed class PricingScheduleOptions
{
    /// <summary>Master toggle for the autonomous daily pricing run. Default false (operator opt-in).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Absolute local path to the workbook to price (e.g. the top-500-dispensed sheet). Required to
    /// run. The pipeline reads NDCs from it and writes a sibling priced workbook next to it.
    /// </summary>
    public string? WorkbookPath { get; set; }

    /// <summary>Local time of day to run, "HH:mm" (24h). Default "02:00" — overnight, off-hours.</summary>
    public string RunAtLocalTime { get; set; } = "02:00";

    /// <summary>NDC column header in the workbook. Default matches the cockpit pricing command.</summary>
    public string NdcColumn { get; set; } = PricingJobDefaults.NdcColumn;

    /// <summary>Header for the supplier column written back. Default matches the cockpit command.</summary>
    public string SupplierColumn { get; set; } = PricingJobDefaults.SupplierColumn;

    /// <summary>Header for the cost column written back. Default matches the cockpit command.</summary>
    public string CostColumn { get; set; } = PricingJobDefaults.CostColumn;
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
