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
    public string Version { get; set; } = "3.0.0";
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
    /// Maximum prescriptions per detection query. Default 100. Increase for high-volume pharmacies.
    /// </summary>
    public int MaxDetectionBatchSize { get; set; } = 100;

    /// <summary>
    /// When true, agent runs in learning mode (30-day observation).
    /// When false, uses the existing PioneerRx adapter directly.
    /// </summary>
    public bool LearningMode { get; set; }

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
