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
    public string Version { get; set; } = "3.9.2";
    public string UpdateChannel { get; set; } = "stable";
    public string? SqlServer { get; set; }
    public string? SqlDatabase { get; set; }
    public string? SqlUser { get; set; }
    public string? SqlPassword { get; set; }

    /// <summary>
    /// When true, SQL connections accept any server certificate (default for pharmacy LAN compatibility).
    /// HIPAA warning logged when enabled. Set to false when SQL Server has a trusted certificate.
    /// </summary>
    public bool SqlTrustServerCertificate { get; set; } = true;

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
    /// v3.12 — autonomous workflow template extraction. Enabled=false by
    /// default; pilot pharmacies opt in. When enabled, LearningWorker runs
    /// WorkflowTemplateExtractor + TemplateRuleGenerator on pattern/model
    /// phase cadence and emits YAML rules to the auto/ directory with
    /// autonomousOk=false (operator approval required).
    /// </summary>
    public TemplateLearningOptions TemplateLearning { get; set; } = new();

    /// <summary>
    /// v3.12 — Fleet Schema Canary propagation. Enabled=false by default
    /// (contract-only in v3.12; cloud endpoint lands v3.12.1).
    /// </summary>
    public FleetFeaturesOptions FleetFeatures { get; set; } = new();

    /// <summary>
    /// Vision pipeline configuration (screenshot capture + extraction). Off by
    /// default — enabling adds a new HIPAA surface (encrypted screens on disk).
    /// </summary>
    public VisionOptions Vision { get; set; } = new();

    /// <summary>
    /// Multi-pharmacy config. When populated, each entry gets its own detection worker.
    /// Backwards-compatible: if empty, falls back to the top-level SqlServer/PharmacyId fields.
    /// </summary>
    public List<PharmacyConfig> Pharmacies { get; set; } = new();

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
                SqlDatabase = SqlDatabase ?? "PioneerPharmacySystem",
                SqlUser = SqlUser,
                SqlPassword = SqlPassword,
            }
        };
    }
}

public sealed class PharmacyConfig
{
    public string PharmacyId { get; set; } = "";
    public string SqlServer { get; set; } = "";
    public string SqlDatabase { get; set; } = "PioneerPharmacySystem";
    public string? SqlUser { get; set; }
    public string? SqlPassword { get; set; }
    public bool Enabled { get; set; } = true;
}
