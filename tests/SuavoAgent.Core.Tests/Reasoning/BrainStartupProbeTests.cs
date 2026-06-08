using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class BrainStartupProbeTests
{
    [Fact]
    public async Task RunAsync_ReachesBrain_NoRules_NoInference()
    {
        var brain = Brain();

        // Probe should complete without throwing. No assertion on decision
        // content beyond that — the probe is a wiring check, not behavioral.
        await BrainStartupProbe.RunAsync(brain, NullLogger.Instance);
    }

    [Fact]
    public async Task RunAsync_SurvivesBrokenBrain()
    {
        // If DecideAsync throws for any reason, the probe must swallow and
        // continue — failing-open at startup is the correct policy here.
        var brain = new TieredBrain(
            new RuleEngine(Array.Empty<Rule>(), NullLogger<RuleEngine>.Instance),
            new ThrowingInference(),
            new ActionVerifier(),
            NullLogger<TieredBrain>.Instance);

        // TieredBrain catches the exception, probe sees NoMatch — no crash.
        await BrainStartupProbe.RunAsync(brain, NullLogger.Instance);
    }

    [Fact]
    public async Task RunAsync_ShadowModePreventsExecution()
    {
        // Even if Tier 2 proposed an approved Click, shadow mode converts
        // the result to NoMatch — so the probe never "executes" anything.
        var mock = new MockLocalInference();
        mock.EnqueueApproved(RuleActionType.Log, 1.0);

        var brain = new TieredBrain(
            new RuleEngine(Array.Empty<Rule>(), NullLogger<RuleEngine>.Instance),
            mock,
            new ActionVerifier(),
            NullLogger<TieredBrain>.Instance);

        await BrainStartupProbe.RunAsync(brain, NullLogger.Instance);

        // Mock was called (proves escalation reached Tier-2), but nothing
        // "ran" because shadow mode.
        Assert.Equal(1, mock.CallCount);
    }

    [Fact]
    public async Task RunAsync_BoundedEvenWhenInferenceIgnoresCancellation()
    {
        // The live failure (Mina's box 2026-06-05): a grammar-constrained
        // llama.cpp generation spins in native code without yielding a
        // cancellable point, so DecideAsync does not return even after the
        // probe's cooperative timeout. The probe MUST still return on its hard
        // budget — otherwise an awaited probe blocks host.Run() and Core never
        // signals SERVICE_RUNNING -> stuck StartPending -> agent never comes up.
        var brain = new TieredBrain(
            new RuleEngine(Array.Empty<Rule>(), NullLogger<RuleEngine>.Instance),
            new BlockingInference(),
            new ActionVerifier(),
            NullLogger<TieredBrain>.Instance);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await BrainStartupProbe.RunAsync(brain, NullLogger.Instance);
        sw.Stop();

        // Hard WhenAny guard is 3s; assert it does NOT block for the full
        // (60s) stuck inference. Generous headroom for slow CI.
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(15),
            $"Probe blocked {sw.Elapsed.TotalSeconds:F1}s — must return on its budget regardless of a stuck inference.");
    }

    // --- helpers -------------------------------------------------------------

    private static TieredBrain Brain() =>
        new(
            new RuleEngine(Array.Empty<Rule>(), NullLogger<RuleEngine>.Instance),
            new NullLocalInference(),
            new ActionVerifier(),
            NullLogger<TieredBrain>.Instance);

    private sealed class ThrowingInference : ILocalInference
    {
        public string ModelId => "throws";
        public bool IsReady => true;

        public Task<InferenceProposal?> ProposeAsync(InferenceRequest request, CancellationToken ct) =>
            throw new InvalidOperationException("test throw");
        public Task<string?> ChatAsync(string userMessage, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    // Mimics a native llama.cpp generation that does NOT honor the cancellation
    // token (blocks regardless of ct) — the exact behavior that bricked startup.
    private sealed class BlockingInference : ILocalInference
    {
        public string ModelId => "blocks";
        public bool IsReady => true;

        public async Task<InferenceProposal?> ProposeAsync(InferenceRequest request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), CancellationToken.None);
            return null;
        }
        public Task<string?> ChatAsync(string userMessage, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
