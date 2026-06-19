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

    /// <summary>
    /// Nightly stress variant of the concurrent-swap test (Codex Comp 2
    /// chunk 4 round-5 MED acceptance criteria). Runs 30s wall-clock with
    /// 4 reader threads + 1 swapper. Filtered by <c>Category=Stress</c> trait
    /// so it runs in <c>.github/workflows/mesh-stress-nightly.yml</c> only —
    /// NOT in PR CI (which runs the 1s smoke variant above).
    /// </summary>
    /// <remarks>
    /// The acceptance gate is the <b>no-mixed-generation invariant</b>: across
    /// a 30s storm no reader may ever observe a <see cref="RulesetRuntime"/>
    /// whose Ruleset / Scrubber / Fingerprinter disagree on version. That is
    /// the memory-model contract this test exists to defend.
    /// <para>
    /// The swap/read <i>counts</i> are LIVENESS floors only, not throughput
    /// gates. Per-swap cost is dominated by constructing a fresh
    /// <see cref="PhiScrubber"/> + <see cref="FingerprintComputer"/> (and a
    /// guarded journal append) inside <c>Wire.SwapRuleset</c>, so absolute
    /// throughput is entirely runner-bound — a 2-core GitHub-hosted
    /// <c>windows-latest</c> sustains ~200–250 swaps/30s, not the ~10k a fast
    /// dev box hits. Asserting an absolute 10k here made the nightly red every
    /// night regardless of correctness. We now assert only that the swapper
    /// and readers made continuous progress (not starved/deadlocked); true
    /// high-volume throughput validation belongs on dedicated hardware
    /// (tracked follow-up, see workflow header).
    /// </para>
    /// </remarks>
    [Fact]
    [Trait("Category", "Stress")]
    public async Task Stress_Concurrent_swap_storm_30s_no_mixed_generation()
    {
        // Gated by an env var — set ONLY in .github/workflows/mesh-stress-nightly.yml.
        // PR CI runs the 1s smoke variant above; this 30s × 10k variant is the
        // nightly acceptance gate (Codex Comp 2 chunk 4 round-5 MED). The Trait
        // is informational; the env-var check is what actually gates execution.
        if (Environment.GetEnvironmentVariable("MESH_STRESS_NIGHTLY") != "1")
        {
            return;
        }

        EnsureWireInitialised();

        var rulesetA = new RulesetV1
        {
            RulesetVersion = "v100.A",
            RulesetVersionInt = 100,
            KeyId = "stress-A",
            SignedAt = "2026-05-14T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };
        var rulesetB = new RulesetV1
        {
            RulesetVersion = "v200.B",
            RulesetVersionInt = 200,
            KeyId = "stress-B",
            SignedAt = "2026-05-14T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var flags = new StressFlags();
        var swapsAtStart = Wire.RulesetSwapsTotal;

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

        // THE GATE: no reader ever saw a torn cross-generation snapshot.
        Assert.Equal(0, Volatile.Read(ref flags.MixedGenerationDetected));

        // Liveness floors (NOT throughput gates — see remarks). These only
        // prove the swapper and readers made continuous progress across the
        // 30s window rather than starving or deadlocking, which would
        // otherwise let the invariant assert pass vacuously (no swaps → no
        // torn reads possible). Floors sit ~7x below the slowest observed
        // hosted-runner throughput (~200 swaps/30s) so transient contention
        // can't flake them.
        var swapsCompleted = Wire.RulesetSwapsTotal - swapsAtStart;
        Assert.True(swapsCompleted >= 30,
            $"Liveness: swapper made too little progress in 30s; got {swapsCompleted} swaps "
            + "(expected ≥30 — likely starved or deadlocked, not a throughput miss).");
        Assert.True(flags.IterationsObserved >= 10_000,
            $"Liveness: readers made too little progress in 30s; got {flags.IterationsObserved} "
            + "reads (expected ≥10,000 — reads are allocation-free; this floor is trivially met "
            + "unless reader threads never ran).");
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
