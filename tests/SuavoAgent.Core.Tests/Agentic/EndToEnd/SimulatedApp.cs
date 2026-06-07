using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Agentic;

namespace SuavoAgent.Core.Tests.Agentic.EndToEnd;

/// <summary>
/// A tiny deterministic stand-in for a real desktop app: a screen of element signatures that changes
/// when an element is clicked. Lets an end-to-end test drive the REAL agentic actuation pipeline
/// (loop → VerbActuator → VerbDispatcher 7-step flow → gateway) and observe real state changes — the
/// closest functional proof to a live UIA app without the Windows box.
/// </summary>
internal sealed class SimulatedApp
{
    private HashSet<string> _visible;
    private readonly Dictionary<string, HashSet<string>> _onClick;     // automationId → new visible set
    private readonly Dictionary<string, HashSet<string>> _onType;      // automationId → new visible set

    public SimulatedApp(
        IEnumerable<string> initialVisible,
        Dictionary<string, IEnumerable<string>>? onClick = null,
        Dictionary<string, IEnumerable<string>>? onType = null)
    {
        _visible = new HashSet<string>(initialVisible, StringComparer.OrdinalIgnoreCase);
        _onClick = (onClick ?? new()).ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        _onType = (onType ?? new()).ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> VisibleSignatures => _visible;

    public void Click(string automationId)
    {
        if (_onClick.TryGetValue(automationId, out var next)) _visible = new HashSet<string>(next, StringComparer.OrdinalIgnoreCase);
    }

    public void Type(string automationId)
    {
        if (_onType.TryGetValue(automationId, out var next)) _visible = new HashSet<string>(next, StringComparer.OrdinalIgnoreCase);
    }

    public static string Sig(string controlType, string automationId) => $"{controlType}|{automationId}";
}

/// <summary>Production IActuationGateway replaced by one that drives the SimulatedApp (real dispatcher above it).</summary>
internal sealed class SimulatedAppGateway : IActuationGateway
{
    private readonly SimulatedApp _app;
    public SimulatedAppGateway(SimulatedApp app) => _app = app;

    public Task<ActuationGateState> GetStateAsync(CancellationToken ct) =>
        Task.FromResult(new ActuationGateState(true, false, null, null, null));

    public Task<ActuationResult> ClickByLabelAsync(ClickByLabelRequest req, CancellationToken ct)
    {
        _app.Click(req.Label);
        return Task.FromResult(ActuationResult.Success(1, req.DryRun, "evidence"));
    }

    public Task<ActuationResult> ClickBySignatureAsync(ClickBySignatureRequest req, CancellationToken ct)
    {
        if (!req.DryRun) _app.Click(req.AutomationId);
        return Task.FromResult(ActuationResult.Success(1, req.DryRun, "evidence"));
    }

    public Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct) =>
        Task.FromResult(ActuationResult.Success(1, req.DryRun, "evidence"));

    public Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct) =>
        Task.FromResult(ActuationResult.Success(1, req.DryRun, "evidence"));

    public Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct) =>
        Task.FromResult(ActuationResult.Success(1, req.DryRun, "evidence"));

    public Task<ActuationResult> ReloadAllowlistAsync(CancellationToken ct) =>
        Task.FromResult(ActuationResult.Success(0, true, "reload"));
}

/// <summary>IPerceiver that reports the SimulatedApp's current screen (signatures the replayer/loop ground on).</summary>
internal sealed class SimulatedAppPerceiver : IPerceiver
{
    private readonly SimulatedApp _app;
    public SimulatedAppPerceiver(SimulatedApp app) => _app = app;

    public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct)
    {
        var sigs = _app.VisibleSignatures.ToArray();
        var summary = sigs; // for grounding; the replayer matches on Signatures
        var hash = string.Join(",", sigs.OrderBy(s => s, StringComparer.Ordinal));
        return Task.FromResult<PerceivedScreen?>(new PerceivedScreen(hash, Scrubbed: true, summary, WindowTitle: "sim", Signatures: sigs));
    }
}
