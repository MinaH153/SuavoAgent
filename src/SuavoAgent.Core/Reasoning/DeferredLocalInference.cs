using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Local inference that DEFERS construction of the real <see cref="LLamaLocalInference"/> until its
/// assets (the GGUF model + the native llama.cpp DLLs) have finished provisioning. On first run a
/// reasoning-enabled box has neither yet; this kicks the background downloads and returns null
/// proposals/chats (rules-only) until they land — then lazily constructs the real engine on the next
/// call. No second restart, no timing guesswork: a `chat` poll flips from ready=false to a real reply
/// the moment the download completes. The clean "enable → it self-equips → it just works" path.
///
/// Fail-soft throughout: nothing here can block the agent or hinder PioneerRx.
/// </summary>
public sealed class DeferredLocalInference : ILocalInference
{
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly ReasoningOptions _options;
    private readonly NativeLibProvisioner _nativeProvisioner;
    private readonly IModelManager _modelManager;
    private readonly ILogger<LLamaLocalInference> _llamaLogger;
    private readonly ILogger<DeferredLocalInference> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile LLamaLocalInference? _inner;

    public DeferredLocalInference(
        IOptions<AgentOptions> agentOptions,
        NativeLibProvisioner nativeProvisioner,
        IModelManager modelManager,
        ILogger<LLamaLocalInference> llamaLogger,
        ILogger<DeferredLocalInference> logger)
    {
        _agentOptions = agentOptions;
        _options = agentOptions.Value.Reasoning;
        _nativeProvisioner = nativeProvisioner;
        _modelManager = modelManager;
        _llamaLogger = llamaLogger;
        _logger = logger;

        // Kick the one-time background provisioning of BOTH assets immediately so they start landing
        // while the agent runs rules-only. Fire-and-forget; failures leave the brain off.
        _nativeProvisioner.EnsureOrProvision();
        _ = Task.Run(async () =>
        {
            try { await _modelManager.VerifyAsync(CancellationToken.None); } catch { /* fail-soft */ }
        });
    }

    public string ModelId => _options.ModelId;

    // Ready once the ASSETS are present (not only once _inner is built) — callers (e.g. the chat
    // command) gate on IsReady BEFORE invoking, and _inner is constructed lazily INSIDE the first
    // invocation. Reporting ready on assets-present is what lets that first call through to build +
    // load the engine; reporting only on _inner!=null would deadlock (never invoked → never built).
    public bool IsReady => _inner is not null || AssetsPresent();

    private bool AssetsPresent() =>
        _nativeProvisioner.DllsPresent()
        && !string.IsNullOrWhiteSpace(_options.ModelPath)
        && File.Exists(_options.ModelPath);

    /// <summary>Provisioning lifecycle derived from what's on disk — no new counters,
    /// no races with the background downloads. Powers the dashboard Brain card.</summary>
    public BrainProvisioningState ProvisioningState
    {
        get
        {
            if (_inner is not null) return BrainProvisioningState.Ready;
            if (string.IsNullOrWhiteSpace(_options.ModelPath)) return BrainProvisioningState.Off;
            if (!_nativeProvisioner.DllsPresent()) return BrainProvisioningState.DownloadingLibs;
            if (File.Exists(_options.ModelPath)) return BrainProvisioningState.Ready;
            // Libs present, final model file absent: the download is in flight (temp
            // file growing) or about to start — the honest label is "downloading".
            return BrainProvisioningState.DownloadingModel;
        }
    }

    /// <summary>Download percent from the provisioner's temp file size vs the known
    /// model size (baked at install / pushed via set_reasoning_config). Null when
    /// either side is unknown.</summary>
    public int? ProvisioningPercent
    {
        get
        {
            var state = ProvisioningState;
            if (state == BrainProvisioningState.Ready) return 100;
            if (state != BrainProvisioningState.DownloadingModel) return null;
            var total = _options.ModelSizeBytes;
            if (total is null or <= 0 || string.IsNullOrWhiteSpace(_options.ModelPath)) return null;
            try
            {
                var tmp = new FileInfo(_options.ModelPath + ".download");
                if (!tmp.Exists) return 0;
                return (int)Math.Clamp(tmp.Length * 100 / total.Value, 0, 99);
            }
            catch
            {
                return null;
            }
        }
    }

    public async Task<InferenceProposal?> ProposeAsync(InferenceRequest request, CancellationToken ct)
    {
        var inner = await EnsureInnerAsync(ct).ConfigureAwait(false);
        return inner is null ? null : await inner.ProposeAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<string?> ChatAsync(string userMessage, CancellationToken ct)
    {
        var inner = await EnsureInnerAsync(ct).ConfigureAwait(false);
        return inner is null ? null : await inner.ChatAsync(userMessage, ct).ConfigureAwait(false);
    }

    /// <summary>Construct the real engine once both assets are present; null until then.</summary>
    private async Task<LLamaLocalInference?> EnsureInnerAsync(CancellationToken ct)
    {
        if (_inner is not null) return _inner;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null) return _inner;

            if (!_nativeProvisioner.DllsPresent())
            {
                _nativeProvisioner.EnsureOrProvision(); // re-kick if not started
                return null;
            }
            if (string.IsNullOrWhiteSpace(_options.ModelPath) || !File.Exists(_options.ModelPath))
            {
                return null; // model still downloading
            }

            _logger.LogInformation("Local brain assets present — constructing LLamaLocalInference ({Id})", _options.ModelId);
            _inner = new LLamaLocalInference(_agentOptions, _options.ModelPath!, _llamaLogger);
            return _inner;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DeferredLocalInference: failed to construct engine — staying deferred");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }
}
