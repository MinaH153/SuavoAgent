namespace SuavoAgent.Core.Config;

/// <summary>
/// Tier-2 (LocalInference) configuration.
///
/// Default: disabled — agent runs rules-only until the operator explicitly
/// opts in via config + drops the model file in the configured path. This
/// keeps installs small and predictable; power comes online incrementally.
/// </summary>
public sealed class ReasoningOptions
{
    /// <summary>
    /// When false, TieredBrain uses NullLocalInference and every Tier-1 NoMatch
    /// goes straight to the operator. Default false.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Absolute path to the GGUF model file. When null/missing on disk, the
    /// agent logs a warning and falls back to NullLocalInference.
    /// </summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Expected SHA-256 of the model file (lowercase hex). When present,
    /// IModelManager.Verify fails-closed if the hash doesn't match. Guards
    /// against silent corruption or tampered drops.
    /// </summary>
    public string? ModelSha256 { get; set; }

    /// <summary>
    /// Directory holding the native llama.cpp + ggml binaries that LLamaSharp
    /// P/Invokes into. We do NOT ship these by default — their presence is a
    /// vendor fingerprint (Codex C-1). When Tier-2 is enabled the operator
    /// places llama.dll, ggml.dll, and optionally llava_shared.dll here.
    /// Default: %ProgramData%\SuavoAgent\native\ (resolved in DI).
    /// </summary>
    public string? NativeLibraryPath { get; set; }

    /// <summary>
    /// When false (default), any destructive Tier-2 proposal (Click, Type,
    /// PressKey) is routed to the operator approval queue regardless of
    /// confidence. Model-reported confidence alone is not a trust signal
    /// until we have deterministic calibration (Codex M-4). Set true only
    /// when a pharmacy has accepted the risk of auto-executing Tier-2
    /// destructive actions.
    /// </summary>
    public bool AutoExecuteTier2Destructive { get; set; }

    /// <summary>
    /// Friendly id for audit trails — e.g. "llama-3.2-1b-q4_k_m". Bundled into
    /// every InferenceProposal.ModelId so the pattern miner can attribute
    /// decisions to specific model versions.
    /// </summary>
    public string ModelId { get; set; } = "unknown";

    /// <summary>
    /// Default context window. 2048 is the llama.cpp default and plenty for
    /// structured JSON proposals — we don't need long contexts at Tier 2.
    /// </summary>
    public int ContextSize { get; set; } = 2048;

    /// <summary>
    /// Per-proposal token budget. 400 is generous for our schema; cuts off
    /// runaway generation before it wastes wall time.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 400;

    /// <summary>
    /// Keep the model resident in memory for this long after the last call
    /// before unloading. Keeps warm-path latency low while avoiding permanent
    /// ~800 MB RAM occupation on pharmacy PCs that may have 8 GB total.
    /// </summary>
    public int IdleUnloadSeconds { get; set; } = 60;

    /// <summary>
    /// Tier-3 (Cloud Claude) escalation. When true and an agent ApiKey is
    /// present, low-confidence / missing Tier-2 proposals are escalated to
    /// the cloud reasoning endpoint. Default false — opt-in per pharmacy
    /// because it depends on an active Anthropic BAA.
    /// </summary>
    public bool CloudEnabled { get; set; }

    /// <summary>
    /// When true, PricingJobRunner consults the TieredBrain after every NDC
    /// lookup and may Halt the job if the brain returns an Escalate /
    /// AskOperator decision. Default false — opt-in per pharmacy. Tier-1
    /// rules work even without Tier-2/3 enabled; Tier-2/3 still gate on
    /// their own flags.
    /// </summary>
    public bool PricingBrainEnabled { get; set; }
}
