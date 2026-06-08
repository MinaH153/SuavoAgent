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

        // Resolve the chat template + stop strings from the model id. A mismatched template feeds the
        // model special tokens it doesn't know -> it emits its end-of-turn first -> 0 output. The
        // grammar SHOULD mask that, but the win-x64 llama.cpp grammar pass was a no-op on the live box
        // (2026-06-05), so the template itself must elicit valid JSON. (TinyLlama needs Zephyr, not Llama-3.)
        var promptFormat = InferencePromptBuilder.ResolveFormat(ModelId);
        var prompt = InferencePromptBuilder.Build(request, promptFormat);

        var inferenceParams = new InferenceParams
        {
            MaxTokens = _options.MaxOutputTokens,
            AntiPrompts = InferencePromptBuilder.AntiPromptsFor(promptFormat),
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.1f,
                TopK = 40,
                TopP = 0.95f,
                // Best-effort backstop only: the GBNF grammar pass was a no-op on win-x64 (0.19.0,
                // 2026-06-05) and 0.24.0 ships NO documented win-x64 GBNF correctness fix, so it still
                // cannot be relied on to constrain output. The valid-JSON guarantee rests on the
                // model-matched template (above) + ProposalParser (below); grammar tightens output only
                // where the platform honors it. Basic = single-pass (0.19.0 semantics) — avoids 0.24.0's
                // default Extended grammar-resampling path (~5x slower, issue #1099) eating the timeout.
                Grammar = new Grammar(grammar, "root"),
                GrammarOptimization = DefaultSamplingPipeline.GrammarOptimizationMode.Basic,
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
    /// Freeform chat completion — the cockpit "talk to the agent" path. Same load/idle-unload machinery
    /// as <see cref="ProposeAsync"/> but no grammar (plain text out). Returns null on any failure so the
    /// caller falls back gracefully (templated reply / cloud) — the local brain is a bonus, never a
    /// hard dependency. The reply is PHI-scrubbed by the COMMAND handler before it leaves the box.
    /// </summary>
    public async Task<string?> ChatAsync(string userMessage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userMessage)) return null;
        ct.ThrowIfCancellationRequested();

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
        finally { _lock.Release(); }

        var promptFormat = InferencePromptBuilder.ResolveFormat(ModelId);
        // Minimal ChatML — Qwen3's native format. A grounded, concise pharmacy-ops persona.
        var system =
            "You ARE SuavoAgent — an AI agent that runs ON this pharmacy's Windows PC, like a person sitting at the keyboard. " +
            "You can SEE what is on the screen (you read its windows, buttons, and text through the accessibility layer) and you can CONTROL the mouse and keyboard to open apps, click, type, and carry out tasks on this computer. " +
            "Always answer in the first person about these REAL abilities — NEVER say you 'cannot see the screen', that you 'can't access displays', or that you are 'just a language model'. You are an on-device agent with eyes (the screen) and hands (mouse + keyboard). " +
            "Keep replies short and plain. Never invent patient data, prices, or numbers, and never claim you performed an action unless it actually happened — state only what is real.";
        // Prefill an empty think-block so Qwen3 runs in NON-thinking mode (fast, no <think> ramble) —
        // the documented Qwen3 convention. We also strip any <think>…</think> from the reply as a
        // belt-and-suspenders for builds that emit it anyway.
        var prompt =
            $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{userMessage.Trim()}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n";

        var inferenceParams = new InferenceParams
        {
            MaxTokens = Math.Min(_options.MaxOutputTokens, 256),
            AntiPrompts = new[] { "<|im_end|>", "<|im_start|>" },
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.6f,
                TopK = 40,
                TopP = 0.95f,
                // Curb the small-model repetition loop. Confirmed live: some prompts (e.g. "what's my
                // name") sent Qwen3-1.7B into an unbounded generation that never finished on the box's
                // weak CPU. A repeat penalty breaks the loop so the reply terminates promptly.
                RepeatPenalty = 1.15f,
                PenaltyCount = 256,
            },
        };

        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        // HARD wall-clock ceiling. The token we hand InferAsync is best-effort only: on win-x64 the
        // native loop does NOT reliably observe cancellation mid-generation (confirmed live — a hung
        // prompt ran far past this timeout and never acked). So we ALSO race the whole consumption
        // against a real Task.Delay and ABANDON a stuck inference rather than block the caller forever.
        var ceiling = TimeSpan.FromSeconds(Math.Max(20, _options.InferenceTimeoutSeconds * 3));
        var timeoutCts = new CancellationTokenSource(ceiling);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        // Own the in-flight accounting on the REAL inference lifetime (not the caller's), so an
        // abandoned-but-still-running generation keeps the model pinned against idle-unload until it
        // truly ends — and the decrement happens exactly once whether we wait it out or abandon it.
        async Task ConsumeAsync()
        {
            try
            {
                await foreach (var token in executor.InferAsync(prompt, inferenceParams, linked.Token).ConfigureAwait(false))
                {
                    sb.Append(token);
                    if (sb.Length > 4096) break;
                }
            }
            finally
            {
                _lastUse = DateTime.UtcNow;
                Interlocked.Decrement(ref _activeInferences);
                RestartIdleWatcher();
                timeoutCts.Dispose();
                linked.Dispose();
            }
        }

        try
        {
            var consume = Task.Run(ConsumeAsync, CancellationToken.None);
            var finished = await Task.WhenAny(consume, Task.Delay(ceiling, CancellationToken.None)).ConfigureAwait(false);
            if (finished != consume)
            {
                // Wall-clock hit: the native loop is ignoring cancellation. Best-effort cancel, then
                // detach — swallow the orphan's eventual exception so it can't crash the process — and
                // fall back. We do NOT read `sb` here (the orphan may still be appending to it).
                try { linked.Cancel(); } catch (ObjectDisposedException) { /* orphan already finished + disposed */ }
                _ = consume.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                _logger.LogWarning("LLamaLocalInference.Chat: hard timeout after {Ms}ms — abandoning inference", sw.ElapsedMilliseconds);
                return null;
            }
            await consume.ConfigureAwait(false); // observe completion / exceptions
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("LLamaLocalInference.Chat: timed out after {Ms}ms", sw.ElapsedMilliseconds);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLamaLocalInference.Chat: inference error");
            return null;
        }
        finally
        {
            sw.Stop();
        }

        var reply = sb.ToString().Replace("<|im_end|>", "").Replace("<|im_start|>", "");
        // Strip any Qwen3 <think>…</think> reasoning block so the cockpit shows only the answer.
        reply = System.Text.RegularExpressions.Regex.Replace(reply, @"<think>.*?</think>", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        reply = reply.Replace("<think>", "").Replace("</think>", "").Trim();
        _logger.LogInformation("LLamaLocalInference.Chat: {Len} chars in {Ms}ms", reply.Length, sw.ElapsedMilliseconds);
        return reply.Length == 0 ? null : reply;
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

                    // LLamaSharp 0.24.0's llama.dll has hard load-time deps on ggml.dll + ggml-base.dll +
                    // ggml-cpu.dll in the SAME folder (llama.cpp split the ggml backend). Name any missing
                    // one up-front so a model-load failure reads as "ggml-cpu.dll missing" rather than an
                    // opaque LoadLibrary dependency error. (llava_shared.dll is NOT checked — it is OPTIONAL,
                    // only needed for the future multimodal/vision path; text-only inference loads without it.)
                    var missingNatives = new[] { "ggml.dll", "ggml-base.dll", "ggml-cpu.dll", "llama.dll" }
                        .Where(n => !File.Exists(Path.Combine(_options.NativeLibraryPath, n)))
                        .ToArray();
                    if (missingNatives.Length > 0)
                        _logger.LogWarning(
                            "LLamaLocalInference: NativeLibraryPath {Path} is missing required native libs [{Missing}] — model load will fail (0.24.0 text-only needs ggml.dll, ggml-base.dll, ggml-cpu.dll, llama.dll; llava_shared.dll is optional, multimodal-only)",
                            _options.NativeLibraryPath, string.Join(", ", missingNatives));

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

            // Cap inference threads to protect PioneerRx on a 4-core i5: leaving cores free for the PMS
            // matters more than a faster reply. 0 (auto) = half the cores, min 1.
            var coreCount = Environment.ProcessorCount;
            var threads = _options.CpuThreads > 0
                ? Math.Min(_options.CpuThreads, coreCount)
                : Math.Max(1, coreCount / 2);
            var parameters = new ModelParams(_modelPath)
            {
                ContextSize = (uint)_options.ContextSize,
                GpuLayerCount = 0, // pharmacy PCs have no GPU
                UseMemorymap = true,
                Threads = threads,        // never starve PioneerRx of CPU
                BatchThreads = threads,
            };
            _logger.LogInformation(
                "LLamaLocalInference: loading with {Threads}/{Cores} CPU threads (PioneerRx-protective cap)",
                threads, coreCount);

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
