using System;
using System.Collections.Generic;

namespace SuavoAgent.Helper.Security;

/// <summary>
/// The testable heart of the honeytoken watcher: turns a raw file-touch into a corroborated, de-duplicated
/// ladder decision and applies it via the orchestrator.
///
/// Two safety properties live here:
///   1. FSW-STORM DEDUP — <c>FileSystemWatcher</c> fires several events for one logical access; without
///      dedup a single backup read could be counted as a "repeat" and false-escalate Degrade→Apoptosis.
///      Rapid events for the same toucher within <see cref="_dedupWindow"/> collapse to ONE distinct touch.
///   2. FAIL-OPEN — every callback is wrapped: a bug here must NEVER quarantine a live pharmacy, so any
///      exception is swallowed (the gate is left untouched).
///
/// The FileSystemWatcher itself is a thin OS shell (<c>HoneytokenWatcher</c>) that just forwards to
/// <see cref="OnTouch"/>; all the logic + state is here, so it is fully unit-testable headless.
/// </summary>
public sealed class HoneytokenReflex
{
    private readonly HoneytokenCorroborator _corroborator;
    private readonly ApoptosisOrchestrator _orchestrator;
    private readonly IFileAccessAttributor _attributor;
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _dedupWindow;
    private readonly Action<CorroborationResult>? _onSignal;

    private readonly object _sync = new();
    private readonly Dictionary<string, (int Count, DateTimeOffset Last)> _touches =
        new(StringComparer.OrdinalIgnoreCase);

    public HoneytokenReflex(
        HoneytokenCorroborator corroborator,
        ApoptosisOrchestrator orchestrator,
        IFileAccessAttributor attributor,
        Func<DateTimeOffset>? now = null,
        TimeSpan? dedupWindow = null,
        Action<CorroborationResult>? onSignal = null)
    {
        _corroborator = corroborator ?? throw new ArgumentNullException(nameof(corroborator));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _attributor = attributor ?? throw new ArgumentNullException(nameof(attributor));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _dedupWindow = dedupWindow ?? TimeSpan.FromSeconds(1);
        _onSignal = onSignal;
    }

    /// <summary>Handle one raw honeytoken touch. Idempotent under FSW storms; fail-open on any error.</summary>
    public void OnTouch(string path)
    {
        try
        {
            var toucher = _attributor.Attribute(path);
            var key = string.IsNullOrWhiteSpace(toucher.ProcessName) ? "__unknown__" : toucher.ProcessName!;
            var now = _now();

            int priorTouchCount;
            lock (_sync)
            {
                if (_touches.TryGetValue(key, out var e))
                {
                    if (now - e.Last < _dedupWindow)
                    {
                        // Same logical access still firing FSW events — slide the window, do NOT re-count.
                        _touches[key] = (e.Count, now);
                        return;
                    }
                    priorTouchCount = e.Count;
                    _touches[key] = (e.Count + 1, now);
                }
                else
                {
                    priorTouchCount = 0;
                    _touches[key] = (1, now);
                }
            }

            var result = _corroborator.Corroborate(toucher.ProcessName, toucher.ExePath, priorTouchCount);
            _orchestrator.OnCompromise(result);
            _onSignal?.Invoke(result);
        }
        catch
        {
            // FAIL-OPEN: never let a reflex bug brick a live pharmacy. Leave the gate untouched.
        }
    }
}
