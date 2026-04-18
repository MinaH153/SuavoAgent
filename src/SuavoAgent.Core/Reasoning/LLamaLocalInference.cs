using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
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
///
/// Safety:
///   - Never throws for inference failures — returns null so TieredBrain cleanly
///     escalates to the operator queue
///   - Hard MaxTokens cap prevents runaway generation wasting wall time
///   - Single-flight lock prevents concurrent model loads/unloads
///   - Temperature 0.1 = near-deterministic; sampling only breaks ties
///
/// HIPAA:
///   - Input prompts are already PHI-scrubbed at the extraction boundary
///     in Helper before RuleContext is constructed
///   - Model weights stay on the machine; nothing crosses network
/// </summary>
public sealed class LLamaLocalInference : ILocalInference, IAsyncDisposable
{
    private readonly ReasoningOptions _options;
    private readonly string _modelPath;
    private readonly ILogger<LLamaLocalInference> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private DateTime _lastUse = DateTime.MinValue;
    private CancellationTokenSource? _idleWatcherCts;

    public string ModelId => _options.ModelId;
    public bool IsReady => _weights != null;

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

        if (!await EnsureLoadedAsync(ct)) return null;

        string grammar;
        try
        {
            grammar = ActionGrammar.BuildProposalGrammar(request.AllowedActions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLamaLocalInference: grammar build failed");
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

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(request.Timeout);

            await foreach (var token in _executor!.InferAsync(prompt, inferenceParams, linked.Token))
            {
                sb.Append(token);
                if (sb.Length > 8192) break; // hard safety cap regardless of MaxTokens
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("LLamaLocalInference: inference cancelled/timed out after {Ms}ms",
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
            // Kick off (or reset) idle-unload watcher so we don't keep 800 MB
            // resident forever after a one-shot call.
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

    private async Task<bool> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_weights != null && _executor != null) return true;

        await _lock.WaitAsync(ct);
        try
        {
            if (_weights != null && _executor != null) return true;

            if (!File.Exists(_modelPath))
            {
                _logger.LogError("LLamaLocalInference: model file vanished at {Path}", _modelPath);
                return false;
            }

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
        catch (Exception ex)
        {
            _logger.LogError(ex, "LLamaLocalInference: model load failed");
            _weights?.Dispose();
            _weights = null;
            _executor = null;
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void RestartIdleWatcher()
    {
        _idleWatcherCts?.Cancel();
        _idleWatcherCts = new CancellationTokenSource();
        var token = _idleWatcherCts.Token;
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
        _idleWatcherCts?.Cancel();
        _idleWatcherCts?.Dispose();

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
