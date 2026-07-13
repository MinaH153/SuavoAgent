using System.Collections.Immutable;
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
public sealed class DeferredLocalInference : ILocalInference, IAsyncDisposable
{
    private readonly IOptions<AgentOptions> _agentOptions;
    private readonly ReasoningOptions _options;
    private readonly NativeLibProvisioner _nativeProvisioner;
    private readonly IModelManager _modelManager;
    private readonly ILogger<LLamaLocalInference> _llamaLogger;
    private readonly ILogger<DeferredLocalInference> _logger;
    private readonly string _dataDirectory;
    private readonly IReadOnlyDictionary<string, string> _trustedPublisherKeys;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<string, ILocalInference> _inferenceFactory;
    private ImmutableList<ILocalInference> _retiredInferences =
        ImmutableList<ILocalInference>.Empty;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile ILocalInference? _inner;
    private int _cohortVerificationState;

    public DeferredLocalInference(
        IOptions<AgentOptions> agentOptions,
        NativeLibProvisioner nativeProvisioner,
        IModelManager modelManager,
        ILogger<LLamaLocalInference> llamaLogger,
        ILogger<DeferredLocalInference> logger,
        string? dataDirectoryOverride = null,
        IReadOnlyDictionary<string, string>? trustedPublisherKeys = null,
        Func<DateTimeOffset>? clock = null,
        Func<string, ILocalInference>? inferenceFactory = null)
    {
        _agentOptions = agentOptions;
        _options = agentOptions.Value.Reasoning;
        _nativeProvisioner = nativeProvisioner;
        _modelManager = modelManager;
        _llamaLogger = llamaLogger;
        _logger = logger;
        _dataDirectory = dataDirectoryOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent");
        _trustedPublisherKeys = trustedPublisherKeys ??
                                BrainCohortContract.ProductionTrustedPublisherKeys;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _inferenceFactory = inferenceFactory ?? (modelPath =>
            new LLamaLocalInference(
                _agentOptions,
                modelPath,
                _llamaLogger,
                async ct => (await VerifyCohortAsync(ct).ConfigureAwait(false)).IsValid));

        // Kick the one-time background provisioning of BOTH assets immediately so they start landing
        // while the agent runs rules-only. Fire-and-forget; failures leave the brain off.
        _nativeProvisioner.EnsureOrProvision();
        _ = Task.Run(async () =>
        {
            try { await _modelManager.VerifyAsync(CancellationToken.None); } catch { /* fail-soft */ }
        });
        _ = ObserveCohortVerificationAsync();
    }

    public string ModelId => _options.ModelId;

    // Ready once the ASSETS are present (not only once _inner is built) — callers (e.g. the chat
    // command) gate on IsReady BEFORE invoking, and _inner is constructed lazily INSIDE the first
    // invocation. Reporting ready on assets-present is what lets that first call through to build +
    // load the engine; reporting only on _inner!=null would deadlock (never invoked → never built).
    public bool IsReady => IsPublisherAuthorized() && _inner switch
    {
        LLamaLocalInference llama => !llama.LoadHasFailed,
        not null => true,
        _ => Volatile.Read(ref _cohortVerificationState) == 1,
    };

    /// <summary>Provisioning lifecycle derived from what's on disk — no new counters,
    /// no races with the background downloads. Powers the dashboard Brain card.</summary>
    public BrainProvisioningState ProvisioningState
    {
        get
        {
            if (!IsPublisherAuthorized()) return BrainProvisioningState.Failed;
            if (_inner is not null)
                return _inner is LLamaLocalInference { LoadHasFailed: true }
                    ? BrainProvisioningState.Failed
                    : BrainProvisioningState.Ready;
            if (string.IsNullOrWhiteSpace(_options.ModelPath)) return BrainProvisioningState.Off;
            if (!_nativeProvisioner.DllsPresent()) return BrainProvisioningState.DownloadingLibs;
            if (File.Exists(_options.ModelPath))
                return Volatile.Read(ref _cohortVerificationState) == 1
                    ? BrainProvisioningState.Ready
                    : BrainProvisioningState.Failed;
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
    private async Task<ILocalInference?> EnsureInnerAsync(CancellationToken ct)
    {
        var authorization = ValidatePublisherAuthorization();
        if (!authorization.IsValid)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var retired = Interlocked.Exchange(ref _inner, null);
                if (retired is not null)
                    ImmutableInterlocked.Update(
                        ref _retiredInferences,
                        static (items, value) => items.Add(value),
                        retired);
                Volatile.Write(ref _cohortVerificationState, -1);
            }
            finally
            {
                _lock.Release();
            }
            _logger.LogError(
                "Local Brain publisher authorization rejected ({Code}); refusing inference",
                authorization.Code);
            return null;
        }
        if (_inner is not null) return _inner;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inner is not null) return _inner;

            authorization = ValidatePublisherAuthorization();
            if (!authorization.IsValid)
            {
                Volatile.Write(ref _cohortVerificationState, -1);
                return null;
            }

            // Re-prove the retained package, its derived native DLL
            // inventory, and the GGUF immediately before LLama can resolve or
            // load native/model bytes. Constructor fire-and-forget checks are
            // never an activation signal.
            var cohort = await VerifyCohortAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _cohortVerificationState, cohort.IsValid ? 1 : -1);
            if (!cohort.IsValid)
            {
                _logger.LogError(
                    "Local Brain cohort verification rejected ({Code}); refusing inference",
                    cohort.Code);
                return null;
            }

            if (!_nativeProvisioner.DllsPresent())
            {
                _nativeProvisioner.EnsureOrProvision(); // re-kick if not started
                return null;
            }
            if (string.IsNullOrWhiteSpace(_options.ModelPath) || !File.Exists(_options.ModelPath))
            {
                // Re-kick the model provisioner. A transient first-boot download failure otherwise
                // leaves the GGUF unprovisioned until a full process restart (the provisioner's
                // once-per-process latch never re-fires and VerifyAsync is called only at ctor).
                // Mirrors the native-libs re-kick above; fire-and-forget, fail-soft.
                _ = Task.Run(async () =>
                {
                    try { await _modelManager.VerifyAsync(CancellationToken.None); } catch { /* fail-soft */ }
                });
                return null; // model still downloading
            }

            _logger.LogInformation("core.local_inference.assets_ready");
            _inner = _inferenceFactory(_options.ModelPath!);
            return _inner;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var inner = _inner;
        if (inner is IAsyncDisposable disposable)
            await disposable.DisposeAsync().ConfigureAwait(false);
        var retiredInferences = Interlocked.Exchange(
            ref _retiredInferences,
            ImmutableList<ILocalInference>.Empty);
        foreach (var retired in retiredInferences)
            if (retired is IAsyncDisposable retiredDisposable)
                await retiredDisposable.DisposeAsync().ConfigureAwait(false);
        _lock.Dispose();
    }

    private async Task ObserveCohortVerificationAsync()
    {
        try
        {
            var result = await VerifyCohortAsync(CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _cohortVerificationState, result.IsValid ? 1 : -1);
        }
        catch
        {
            Volatile.Write(ref _cohortVerificationState, -1);
        }
    }

    private Task<InstalledBrainCohortVerification> VerifyCohortAsync(CancellationToken ct)
    {
        try
        {
            return InstalledBrainCohortVerifier.VerifyAsync(
                BrainCohortContract.GetCohortRoot(
                    _dataDirectory,
                    _options.CohortId ?? string.Empty),
                _options.PublisherManifest(),
                _trustedPublisherKeys,
                _clock(),
                ct);
        }
        catch (ArgumentException)
        {
            return Task.FromResult(
                new InstalledBrainCohortVerification(false, "cohort_config_invalid"));
        }
    }

    private BrainCohortValidationResult ValidatePublisherAuthorization() =>
        InstalledBrainCohortVerifier.ValidateAuthorizationForInstalledCohort(
            _options.PublisherManifest(),
            _trustedPublisherKeys,
            _clock());

    private bool IsPublisherAuthorized() => ValidatePublisherAuthorization().IsValid;
}
