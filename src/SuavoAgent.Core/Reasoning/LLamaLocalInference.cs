using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Real Tier-2 implementation backed by llama.cpp via LLamaSharp. Runs the
/// configured GGUF model on CPU with GBNF grammar-constrained decoding so
/// output CANNOT leave the RuleActionSpec JSON schema.
///
/// Lifecycle:
///   - Model lazily loaded on first ProposeAsync call (2–5 s on CPU)
///   - Kept resident for ReasoningOptions.IdleUnloadSeconds between calls
///   - Unloaded when idle to free RAM (~800 MB for Llama-3.2-1B Q4)
///   - Unload refuses while any inference is in-flight (Codex C-2)
///
/// Safety:
///   - Never throws for inference failures — returns null so TieredBrain cleanly
///     escalates to the operator queue
///   - CALLER cancellation DOES propagate (Codex M-2). Only internal timeouts
///     are swallowed.
///   - Hard MaxTokens cap prevents runaway generation wasting wall time
///   - Single-flight lock prevents concurrent model loads/unloads
///   - Temperature 0.1 = near-deterministic; sampling only breaks ties
///
/// HIPAA:
///   - Input prompts are already PHI-scrubbed at the extraction boundary
///     in Helper before RuleContext is constructed
///   - Model weights stay on the machine; nothing crosses network
///
/// Vendor stealth (Codex C-1):
///   - Native llama.cpp binaries are NOT bundled. Operator provides them at
///     ReasoningOptions.NativeLibraryPath and we tell LLamaSharp to load from
///     there via NativeLibraryConfig before the first native call.
/// </summary>
public sealed class LLamaLocalInference : ILocalInference, IAsyncDisposable
{
    private readonly ReasoningOptions _options;
    private readonly string _modelPath;
    private readonly ILogger<LLamaLocalInference> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static int s_nativeConfigured; // 0 = not yet, 1 = done

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private DateTime _lastUse = DateTime.MinValue;
    private int _activeInferences;
    private CancellationTokenSource? _idleWatcherCts;

    public string ModelId => _options.ModelId;

    /// <summary>
    /// IsReady = "configured and verified" not "currently resident in RAM".
    /// Returns true as soon as the DI factory has verified the model file;
    /// the actual weights load lazily inside ProposeAsync (Codex M-1).
    /// </summary>
    public bool IsReady => true;

    public LLamaLocalInference(
        IOptions<AgentOptions> agentOptions,
        string modelPath,
        ILogger<LLamaLocalInference> logger)
    {
        _options = agentOptions.Value.Reasoning;
        _modelPath = modelPath;
        _logger = logger;
    }

    public async Task<InferenceProposal?> ProposeAsync(InferenceRequest request, CancellationToken ct)
    {
        if (request.AllowedActions.Count == 0)
        {
            _logger.LogWarning("LLamaLocalInference: request has no allowed actions, returning null");
            return null;
        }

        // Caller cancellation must propagate unchanged. Use OperationCanceledException
        // discriminator below to distinguish caller-cancel from internal timeout.
        ct.ThrowIfCancellationRequested();

        // Capture local executor reference under lock AND bump active counter
        // atomically so the idle watcher cannot unload underneath us.
        StatelessExecutor? executor;
        await _lock.WaitAsync(ct);
        try
        {
            if (!await EnsureLoadedLockedAsync(ct)) return null;
            executor = _executor;
            if (executor == null) return null;
            Interlocked.Increment(ref _activeInferences);
            _lastUse = DateTime.UtcNow;
        }
        finally
        {
            _lock.Release();
        }

        string grammar;
        try
        {
            grammar = ActionGrammar.BuildProposalGrammar(request.AllowedActions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLamaLocalInference: grammar build failed");
            Interlocked.Decrement(ref _activeInferences);
            return null;
        }

        var prompt = InferencePromptBuilder.Build(request);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _options.MaxOutputTokens,
            AntiPrompts = new[] { "<|eot_id|>", "<|end_of_text|>" },
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.1f,
                TopK = 40,
                TopP = 0.95f,
                Grammar = new Grammar(grammar, "root"),
            },
        };

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();

        // Internal timeout token — distinct from the caller's token so we can
        // tell them apart below.
        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            await foreach (var token in executor.InferAsync(prompt, inferenceParams, linked.Token))
            {
                sb.Append(token);
                if (sb.Length > 8192) break; // hard safety cap regardless of MaxTokens
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancellation — propagate. TieredBrain also catches and
            // correctly distinguishes in its wrapper.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("LLamaLocalInference: inference timed out after {Ms}ms",
                sw.ElapsedMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLamaLocalInference: inference error");
            return null;
        }
        finally
        {
            sw.Stop();
            _lastUse = DateTime.UtcNow;
            Interlocked.Decrement(ref _activeInferences);
            RestartIdleWatcher();
        }

        var proposal = ProposalParser.TryParse(sb.ToString(), ModelId, sw.ElapsedMilliseconds);
        if (proposal == null)
        {
            _logger.LogWarning("LLamaLocalInference: proposal did not parse — {Len} chars in {Ms}ms",
                sb.Length, sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation(
                "LLamaLocalInference: {Action} @ {Conf:F2} in {Ms}ms",
                proposal.Action.Type, proposal.Confidence, sw.ElapsedMilliseconds);
        }
        return proposal;
    }

    /// <summary>
    /// Loads weights if not already loaded. MUST be called with _lock held.
    /// Does not swallow OperationCanceledException from the caller's token.
    /// </summary>
    private async Task<bool> EnsureLoadedLockedAsync(CancellationToken ct)
    {
        if (_weights != null && _executor != null) return true;

        if (!File.Exists(_modelPath))
        {
            _logger.LogError("LLamaLocalInference: model file vanished at {Path}", _modelPath);
            return false;
        }

        // Tell LLamaSharp to load its native binaries from the operator-provided
        // path (Codex C-1). Only call once per process — the config is global.
        if (Interlocked.CompareExchange(ref s_nativeConfigured, 1, 0) == 0)
        {
            try
            {
                if (!string.IsNullOrEmpty(_options.NativeLibraryPath))
                {
                    var llamaPath = Path.Combine(_options.NativeLibraryPath, "llama.dll");
                    var llavaPath = Path.Combine(_options.NativeLibraryPath, "llava_shared.dll");
                    NativeLibraryConfig.All.WithLibrary(
                        File.Exists(llamaPath) ? llamaPath : null,
                        File.Exists(llavaPath) ? llavaPath : null);
                    _logger.LogInformation(
                        "LLamaLocalInference: native library path set to {Path}",
                        _options.NativeLibraryPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLamaLocalInference: failed to configure native library path");
                // Reset so a later call can try again
                Interlocked.Exchange(ref s_nativeConfigured, 0);
            }
        }

        try
        {
            _logger.LogInformation("LLamaLocalInference: loading model from {Path}", _modelPath);
            var sw = Stopwatch.StartNew();

            var parameters = new ModelParams(_modelPath)
            {
                ContextSize = (uint)_options.ContextSize,
                GpuLayerCount = 0, // pharmacy PCs have no GPU
                UseMemorymap = true,
            };

            _weights = await LLamaWeights.LoadFromFileAsync(parameters, ct);
            _executor = new StatelessExecutor(_weights, parameters);

            sw.Stop();
            _logger.LogInformation(
                "LLamaLocalInference: model loaded in {Ms}ms ({ModelId})",
                sw.ElapsedMilliseconds, ModelId);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancel: propagate. Don't silently mark load as failed.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLamaLocalInference: model load failed");
            _weights?.Dispose();
            _weights = null;
            _executor = null;
            return false;
        }
    }

    private void RestartIdleWatcher()
    {
        // Dispose the previous CTS before replacing it, otherwise we leak
        // canceled CancellationTokenSource instances on every call (Codex
        // suggestion).
        var previous = Interlocked.Exchange(ref _idleWatcherCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var token = _idleWatcherCts!.Token;
        var idleAfter = TimeSpan.FromSeconds(Math.Max(10, _options.IdleUnloadSeconds));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(idleAfter, token);
                await UnloadIfIdleAsync(idleAfter);
            }
            catch (OperationCanceledException) { /* another call came in */ }
        }, token);
    }

    private async Task UnloadIfIdleAsync(TimeSpan idleAfter)
    {
        await _lock.WaitAsync();
        try
        {
            // Never unload while any inference is in flight (Codex C-2).
            if (Volatile.Read(ref _activeInferences) > 0) return;
            if (DateTime.UtcNow - _lastUse < idleAfter) return;
            if (_weights == null) return;

            _logger.LogInformation("LLamaLocalInference: unloading model after {S}s idle",
                idleAfter.TotalSeconds);
            _weights.Dispose();
            _weights = null;
            _executor = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _idleWatcherCts, null);
        cts?.Cancel();
        cts?.Dispose();

        await _lock.WaitAsync();
        try
        {
            _weights?.Dispose();
            _weights = null;
            _executor = null;
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}
