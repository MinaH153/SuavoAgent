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
    /// URL to auto-download the GGUF from on first run when it's absent at <see cref="ModelPath"/> —
    /// this is what makes the local brain ship with a CLIENT install instead of a manual drop. The
    /// download is SHA256-verified against <see cref="ModelSha256"/> before use, and failure is
    /// non-fatal (reasoning stays off; never blocks the agent or hinders PioneerRx). Empty = verify
    /// an operator-placed file only (legacy behaviour).
    /// </summary>
    public string? ModelUrl { get; set; }

    /// <summary>
    /// Expected model file size in bytes. Powers the provisioning-percent telemetry
    /// (temp-file size ÷ this) on the dashboard's Brain card. Optional — null just
    /// means no percent is reported while downloading.
    /// </summary>
    public long? ModelSizeBytes { get; set; }

    /// <summary>
    /// URL to a ZIP of the llama.cpp native DLLs (llama.dll + ggml*.dll + llava_shared.dll for ONE AVX
    /// variant) to auto-download + extract into <see cref="NativeLibraryPath"/> on first run when those
    /// DLLs are absent. The native libs are deliberately NOT shipped in the installer (stealth —
    /// "vendor fingerprint"), so this is how a reasoning-enabled box self-equips. SHA256-verified
    /// against <see cref="NativeLibsSha256"/>; failure is non-fatal (reasoning stays off). Empty =
    /// expect operator-placed DLLs.
    /// </summary>
    public string? NativeLibsUrl { get; set; }

    /// <summary>Expected SHA256 (hex) of the native-libs ZIP from <see cref="NativeLibsUrl"/>.</summary>
    public string? NativeLibsSha256 { get; set; }

    /// <summary>
    /// OPTIONAL AVX2-optimized variant of the native-libs ZIP. <see cref="NativeLibsUrl"/> stays the
    /// universal NOAVX fallback (runs on ANY x64 CPU, ~5-10x slower); this is preferred when the box's
    /// CPU reports <c>System.Runtime.Intrinsics.X86.Avx2.IsSupported</c>. Only <c>ggml-cpu.dll</c> differs
    /// between variants (the AVX2 compute kernel). When set + the CPU supports AVX2, the provisioner
    /// downloads THIS instead, and re-provisions a box already holding the other variant (tracked by a
    /// <c>.variant</c> marker). Empty ⇒ noavx everywhere (today's behavior). SHA256-verified like the base.
    /// </summary>
    public string? NativeLibsUrlAvx2 { get; set; }

    /// <summary>Expected SHA256 (hex) of the AVX2 native-libs ZIP from <see cref="NativeLibsUrlAvx2"/>.</summary>
    public string? NativeLibsSha256Avx2 { get; set; }

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
    /// Friendly id for audit trails — e.g. "qwen3-1.7b" (committed family) or
    /// "qwen3-4b-instruct-2507". Also selects the chat template
    /// (InferencePromptBuilder.ResolveFormat keys off substrings: phi → Phi;
    /// qwen3 (non-instruct) → Qwen3Thinkless [ChatML + empty-&lt;think&gt; prefill = non-thinking];
    /// qwen3-instruct-2507 / qwen2.5 / smollm → ChatML; llama-3 → Llama3; else Zephyr)
    /// and is bundled into every InferenceProposal.ModelId for the pattern miner.
    /// NOTE: a Qwen3 GGUF requires LLamaSharp >= 0.24.0 (Core's pin).
    /// </summary>
    public string ModelId { get; set; } = "unknown";

    /// <summary>
    /// Default context window. 4096 gives a 3B-class model (Phi-3.5) room for the
    /// state JSON + the multi-step prior-actions transcript; bump higher only if a
    /// model + RAM budget warrant it (KV cache scales with this).
    /// </summary>
    public int ContextSize { get; set; } = 4096;

    /// <summary>
    /// Per-proposal token budget. 512 fits a well-reasoned action + rationale from
    /// a 3B model; cuts off runaway generation before it wastes wall time.
    /// </summary>
    public int MaxOutputTokens { get; set; } = 512;

    /// <summary>
    /// Wall-clock budget for a single Tier-2 proposal. A 3B-class model (Phi-3.5)
    /// is ~4–9 s/proposal on a no-GPU pharmacy CPU, so the old 3 s default would
    /// always time out → null → needless operator escalation. 12 s gives the slow
    /// tail headroom while still bounding a hung inference. (The startup probe is
    /// independently walled, so this does not reintroduce the boot hang.)
    /// </summary>
    public int InferenceTimeoutSeconds { get; set; } = 12;

    /// <summary>
    /// Keep the model resident in memory for this long after the last call
    /// before unloading. Keeps warm-path latency low while avoiding permanent
    /// ~800 MB RAM occupation on pharmacy PCs that may have 8 GB total.
    /// </summary>
    public int IdleUnloadSeconds { get; set; } = 60;

    /// <summary>
    /// Max CPU threads llama.cpp may use for inference. THE guardrail that keeps a local model from
    /// starving PioneerRx on a 4-core i5: capping inference to ~half the cores leaves headroom for the
    /// PMS, at the cost of slower generation. 0 = auto = max(1, processorCount/2). Never let inference
    /// take every core — PioneerRx responsiveness is the client's livelihood and wins every contention.
    /// </summary>
    public int CpuThreads { get; set; }

    /// <summary>
    /// Run inference at BelowNormal process/thread priority so the OS scheduler always favours
    /// PioneerRx (and the rest of the desktop) when the CPU is contended. Default true.
    /// </summary>
    public bool BelowNormalPriority { get; set; } = true;

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
