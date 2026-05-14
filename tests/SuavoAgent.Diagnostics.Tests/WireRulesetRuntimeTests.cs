using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// <summary>
/// Wire.SwapRuleset + RulesetRuntime snapshot coherence tests
/// (Codex Comp 2 chunk B HIGH RESOLVED).
/// </summary>
/// <remarks>
/// <para>
/// These tests touch Wire's process-wide static state and are collected
/// under <see cref="WireStateCollection"/> so they run serially. Wire's
/// <c>AttachUnhandledHooks</c> is idempotent — the first call wins for the
/// lifetime of the test process — so each test re-asserts post-init state
/// rather than expecting a fresh init.
/// </para>
/// <para>
/// The smoke variant of the concurrent-swap stress test runs ~200 iterations
/// in &lt;5s. The full 10k×30s nightly variant ships in Comp 2.2 follow-up
/// alongside <c>.github/workflows/mesh-stress-nightly.yml</c>.
/// </para>
/// </remarks>
[Collection("WireState")]
public class WireRulesetRuntimeTests
{
    private static void EnsureWireInitialised()
    {
        // Idempotent — first call across the test process wins. Sentry
        // explicitly disabled so SDK init noise doesn't leak across tests.
        Wire.AttachUnhandledHooks(WireComponent.Core, new WireOptions
        {
            EnableSentry = false,
        });
    }

    [Fact]
    public void CurrentRuntime_after_init_is_non_null_and_coherent()
    {
        EnsureWireInitialised();
        var rt = Wire.CurrentRuntime;

        Assert.NotNull(rt);
        Assert.Equal(rt!.Ruleset.RulesetVersion, rt.Scrubber.RulesetVersion);
        Assert.Equal(rt.Ruleset.RulesetVersion, rt.Fingerprinter.RulesetVersion);
    }

    [Fact]
    public void SwapRuleset_publishes_new_generation_coherently()
    {
        EnsureWireInitialised();
        var beforeSwaps = Wire.RulesetSwapsTotal;
        var beforeRuntime = Wire.CurrentRuntime;
        Assert.NotNull(beforeRuntime);

        var next = new RulesetV1
        {
            RulesetVersion = "v9.99",
            RulesetVersionInt = 999,
            KeyId = "test-swap-key",
            SignedAt = "2026-05-14T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };
        Wire.SwapRuleset(next);

        var afterRuntime = Wire.CurrentRuntime;
        Assert.NotNull(afterRuntime);
        Assert.NotSame(beforeRuntime, afterRuntime);
        Assert.Equal("v9.99", afterRuntime!.Ruleset.RulesetVersion);
        Assert.Equal("v9.99", afterRuntime.Scrubber.RulesetVersion);
        Assert.Equal("v9.99", afterRuntime.Fingerprinter.RulesetVersion);
        Assert.Equal(999, afterRuntime.Ruleset.RulesetVersionInt);
        Assert.Equal("v9.99", Wire.RulesetVersion);
        Assert.Equal(999, Wire.RulesetVersionInt);
        Assert.Equal(beforeSwaps + 1, Wire.RulesetSwapsTotal);
    }

    [Fact]
    public void SwapRuleset_with_null_throws_ArgumentNullException()
    {
        EnsureWireInitialised();
        Assert.Throws<ArgumentNullException>(() => Wire.SwapRuleset(null!));
    }

    private sealed class StressFlags
    {
        public int MixedGenerationDetected;
        public long IterationsObserved;
    }

    [Fact]
    public async Task Concurrent_readers_never_observe_mixed_generation_under_swap_storm()
    {
        EnsureWireInitialised();

        var rulesetA = new RulesetV1
        {
            RulesetVersion = "v100.A",
            RulesetVersionInt = 100,
            KeyId = "test",
            SignedAt = "2026-05-14T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };
        var rulesetB = new RulesetV1
        {
            RulesetVersion = "v200.B",
            RulesetVersionInt = 200,
            KeyId = "test",
            SignedAt = "2026-05-14T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };

        // Smoke variant — 1s wall-clock with 4 reader threads + 1 swapper
        // thread. Nightly variant (in a separate workflow file Comp 2.2
        // ships in follow-up) runs 30s × 10k iterations.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var flags = new StressFlags();

        var swapper = Task.Run(() =>
        {
            var i = 0;
            while (!cts.IsCancellationRequested)
            {
                Wire.SwapRuleset((i++ & 1) == 0 ? rulesetA : rulesetB);
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                var rt = Wire.CurrentRuntime;
                if (rt is null) continue;
                if (rt.Ruleset.RulesetVersion != rt.Scrubber.RulesetVersion
                    || rt.Ruleset.RulesetVersion != rt.Fingerprinter.RulesetVersion)
                {
                    Interlocked.Exchange(ref flags.MixedGenerationDetected, 1);
                    return;
                }
                Interlocked.Increment(ref flags.IterationsObserved);
            }
        })).ToArray();

        await Task.WhenAll(readers.Concat(new[] { swapper }));

        Assert.Equal(0, Volatile.Read(ref flags.MixedGenerationDetected));
        Assert.True(flags.IterationsObserved > 100,
            $"Expected at least 100 concurrent reads; got {flags.IterationsObserved}.");
        Assert.True(Wire.RulesetSwapsTotal > 0,
            $"Expected at least one swap completed; counter shows {Wire.RulesetSwapsTotal}.");
    }
}

/// <summary>
/// xUnit collection definition that serialises tests touching Wire's
/// process-wide static state. Empty body — the attribute alone marks the
/// collection so it runs single-threaded.
/// </summary>
[CollectionDefinition("WireState", DisableParallelization = true)]
public class WireStateCollection
{
}
