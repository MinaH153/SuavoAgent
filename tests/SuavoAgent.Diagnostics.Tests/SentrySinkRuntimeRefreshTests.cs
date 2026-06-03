using System;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// <summary>
/// Comp 2.3 RESOLVED: SentrySink reads scrubber + fingerprinter fresh
/// from <see cref="Wire.CurrentRuntime"/> on every BeforeSend / ApplyScope,
/// so OTA <see cref="Wire.SwapRuleset"/> takes effect on the NEXT outgoing
/// Sentry event.
/// </summary>
/// <remarks>
/// Touches Wire's process-wide static state — joins
/// <see cref="WireStateCollection"/> for serial execution alongside the
/// other Wire-stateful tests. Sentry SDK init is disabled
/// (<see cref="WireOptions.EnableSentry"/> = false) so this test stays
/// isolated from real SDK network behaviour.
/// </remarks>
[Collection("WireState")]
public class SentrySinkRuntimeRefreshTests
{
    private static void EnsureWireInitialised()
    {
        // Idempotent — first call across the test process wins.
        Wire.AttachUnhandledHooks(WireComponent.Core, new WireOptions
        {
            EnableSentry = false,
        });
    }

    private static RulesetV1 MakeRuleset(string version, int versionInt)
        => new()
        {
            RulesetVersion   = version,
            RulesetVersionInt = versionInt,
            KeyId            = "test-comp-2-3",
            SignedAt         = "2026-05-14T00:00:00Z",
            SignatureAlg     = "ECDSA_P256_SHA256",
        };

    [Fact]
    public void CurrentScrubber_returns_Wire_runtime_scrubber_after_init()
    {
        EnsureWireInitialised();

        // Bootstrap refs distinct from Wire's runtime — proves CurrentScrubber()
        // is reading from Wire, not from the ctor args.
        var bootstrapRuleset = MakeRuleset("v0.0-bootstrap", 1);
        var sink = new SentrySink(
            new WireOptions { EnableSentry = false },
            new PhiScrubber(bootstrapRuleset, TimeSpan.FromSeconds(1)),
            new FingerprintComputer(bootstrapRuleset, TimeSpan.FromSeconds(1)));

        var live = sink.CurrentScrubber();
        // Wire's initial runtime uses RulesetV1.LoadEmbedded() — its version
        // is whatever the embedded ruleset-v1.json declares. The exact
        // value doesn't matter; what matters is it is NOT the bootstrap
        // version, proving CurrentScrubber preferred Wire.CurrentRuntime.
        Assert.NotEqual("v0.0-bootstrap", live.RulesetVersion);
        Assert.Equal(Wire.CurrentRuntime!.Scrubber.RulesetVersion, live.RulesetVersion);
    }

    [Fact]
    public void CurrentFingerprinter_returns_Wire_runtime_fingerprinter_after_init()
    {
        EnsureWireInitialised();

        var bootstrapRuleset = MakeRuleset("v0.0-bootstrap", 1);
        var sink = new SentrySink(
            new WireOptions { EnableSentry = false },
            new PhiScrubber(bootstrapRuleset, TimeSpan.FromSeconds(1)),
            new FingerprintComputer(bootstrapRuleset, TimeSpan.FromSeconds(1)));

        var live = sink.CurrentFingerprinter();
        Assert.NotEqual("v0.0-bootstrap", live.RulesetVersion);
        Assert.Equal(Wire.CurrentRuntime!.Fingerprinter.RulesetVersion, live.RulesetVersion);
    }

    [Fact]
    public void SwapRuleset_propagates_to_SentrySink_CurrentScrubber()
    {
        EnsureWireInitialised();

        var bootstrapRuleset = MakeRuleset("v0.0-bootstrap", 1);
        var sink = new SentrySink(
            new WireOptions { EnableSentry = false },
            new PhiScrubber(bootstrapRuleset, TimeSpan.FromSeconds(1)),
            new FingerprintComputer(bootstrapRuleset, TimeSpan.FromSeconds(1)));

        var swappedRuleset = MakeRuleset("v77.refresh-test", 77);
        Wire.SwapRuleset(swappedRuleset);

        var afterSwap = sink.CurrentScrubber();
        Assert.Equal("v77.refresh-test", afterSwap.RulesetVersion);

        // Cross-check: Wire and SentrySink agree on the active version.
        Assert.Equal(Wire.RulesetVersion, afterSwap.RulesetVersion);
    }

    [Fact]
    public void SwapRuleset_propagates_to_SentrySink_CurrentFingerprinter()
    {
        EnsureWireInitialised();

        var bootstrapRuleset = MakeRuleset("v0.0-bootstrap", 1);
        var sink = new SentrySink(
            new WireOptions { EnableSentry = false },
            new PhiScrubber(bootstrapRuleset, TimeSpan.FromSeconds(1)),
            new FingerprintComputer(bootstrapRuleset, TimeSpan.FromSeconds(1)));

        var swappedRuleset = MakeRuleset("v78.refresh-test-fp", 78);
        Wire.SwapRuleset(swappedRuleset);

        var afterSwap = sink.CurrentFingerprinter();
        Assert.Equal("v78.refresh-test-fp", afterSwap.RulesetVersion);
    }

    [Fact]
    public void CurrentScrubber_and_CurrentFingerprinter_return_coherent_pair_after_swap()
    {
        EnsureWireInitialised();

        var bootstrapRuleset = MakeRuleset("v0.0-bootstrap", 1);
        var sink = new SentrySink(
            new WireOptions { EnableSentry = false },
            new PhiScrubber(bootstrapRuleset, TimeSpan.FromSeconds(1)),
            new FingerprintComputer(bootstrapRuleset, TimeSpan.FromSeconds(1)));

        var swappedRuleset = MakeRuleset("v79.pair-coherence", 79);
        Wire.SwapRuleset(swappedRuleset);

        var scr = sink.CurrentScrubber();
        var fp  = sink.CurrentFingerprinter();

        // The scrubber + fingerprinter must reflect the SAME generation,
        // matching the per-signal one-read-per-component contract that
        // RulesetRuntime publishes atomically via Volatile.Write.
        Assert.Equal(scr.RulesetVersion, fp.RulesetVersion);
        Assert.Equal("v79.pair-coherence", scr.RulesetVersion);
    }
}
