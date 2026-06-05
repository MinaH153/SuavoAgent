using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Vision;
using SuavoAgent.Core.Agentic;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// Shared production-backed loop adapters for the real-app (harness) tests: a perceiver over
/// FlaUiElementExtractor, an actuator wired directly to the Helper resolvers + SendInput, and a
/// permissive safety gate. Used by both the full-loop test and the watch-&-learn replay test.
/// (Direct actuation, not via the VerbDispatcher, is deliberate — the dispatcher's notepad/calc
/// process allowlist is a security boundary not weakened for a test; the full dispatcher path is
/// proven separately in the Core e2e suite.)
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RealUiaPerceiver : IPerceiver
{
    private readonly FlaUiElementExtractor _extractor;
    private readonly IntPtr _hwnd;
    public RealUiaPerceiver(FlaUiElementExtractor extractor, IntPtr hwnd) { _extractor = extractor; _hwnd = hwnd; }

    public async Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct)
    {
        var els = await _extractor.ExtractAsync(
            new ScreenBytes(Array.Empty<byte>(), 0, 0, DateTimeOffset.UtcNow, (long)_hwnd), 80, ct);
        var summary = new List<string>();
        var signatures = new List<string>();
        foreach (var e in els)
        {
            summary.Add(string.IsNullOrEmpty(e.Name) ? e.Role : $"{e.Role}:{e.Name}");
            if (!string.IsNullOrEmpty(e.AutomationId)) signatures.Add($"{e.Role}|{e.AutomationId}");
        }
        var hash = string.Join(",", summary.OrderBy(s => s, StringComparer.Ordinal));
        return new PerceivedScreen(hash, Scrubbed: true, summary, WindowTitle: "harness", Signatures: signatures);
    }
}

[SupportedOSPlatform("windows")]
internal sealed class DirectUiaActuator : IActuator
{
    private readonly UiaLabelResolver _label;
    private readonly UiaSignatureResolver _sig;
    private readonly SendInputDriver _driver;
    public DirectUiaActuator(UiaLabelResolver label, UiaSignatureResolver sig, SendInputDriver driver)
    { _label = label; _sig = sig; _driver = driver; }

    public async Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
    {
        if (action.Kind != NextActionKind.Act || action.Parameters is null)
            return new ActOutcome(ActStatus.Failed, "not_an_actuating_action");

        var p = action.Parameters;
        string Param(string k) => p.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
        var proc = Param("process_name");

        if (action.Verb == "click_by_label")
        {
            var t = _label.Resolve(Param("label"), proc, UiaLabelResolver.MatchMode.Exact, TimeSpan.FromSeconds(5));
            if (t is null) return new ActOutcome(ActStatus.Failed, "label_not_resolved");
            var r = await _driver.ClickAtAsync(t.X, t.Y, dryRun: false, ct);
            return r.Ok ? new ActOutcome(ActStatus.Success, EvidenceHash: r.EvidenceHash) : new ActOutcome(ActStatus.Failed, r.RejectionCode);
        }
        if (action.Verb == "click_by_signature")
        {
            var t = _sig.Resolve(Param("controlType"), Param("automationId"), null, proc, TimeSpan.FromSeconds(5));
            if (t is null) return new ActOutcome(ActStatus.Failed, "signature_not_resolved");
            var r = await _driver.ClickAtAsync(t.X, t.Y, dryRun: false, ct);
            return r.Ok ? new ActOutcome(ActStatus.Success, EvidenceHash: r.EvidenceHash) : new ActOutcome(ActStatus.Failed, r.RejectionCode);
        }
        return new ActOutcome(ActStatus.Failed, "unsupported_verb");
    }
}

internal sealed class AllowGate : ISafetyGate
{
    public SafetyVerdict Preflight(AgentObjective objective) => SafetyVerdict.Allow;
    public SafetyVerdict GateAction(NextAction action, AgentObjective objective) => SafetyVerdict.Allow;
    public bool AssertScrubbed(PerceivedScreen screen) => true; // the harness has no PHI; perception is GREEN-tier
}
