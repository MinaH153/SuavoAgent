using System;
using System.Threading;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>The presence brain. Decides what to render based on preferences and
/// drives the renderer. NEVER throws into the caller — every render call is
/// try-caught — so actuation is never blocked or failed by a visual.</summary>
public sealed class PresenceController
{
    private readonly IPresenceRenderer _renderer;
    private readonly PresencePreferenceStore _store;
    private readonly ILogger _logger;
    private readonly Func<bool> _isSessionInteractive;
    private readonly IBubbleRenderer? _bubble;
    private readonly IGlowRenderer? _glow;
    private readonly Func<DateTimeOffset> _now;
    private static readonly TimeSpan DrivingWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ObserveWindow = TimeSpan.FromSeconds(8);
    private string? _bubbleText;
    private long _lastAgentTicks;
    private long _lastHumanTicks;
    private volatile int _mode = (int)PresenceMode.Idle;
    private readonly object _lock = new();
    private bool _placed;
    private int _lastX, _lastY;

    public PresenceController(
        IPresenceRenderer renderer,
        PresencePreferenceStore store,
        ILogger logger,
        Func<bool>? isSessionInteractive = null,
        IBubbleRenderer? bubble = null,
        IGlowRenderer? glow = null,
        Func<DateTimeOffset>? clock = null)
    {
        _renderer = renderer;
        _store = store;
        _logger = logger.ForContext<PresenceController>();
        _isSessionInteractive = isSessionInteractive ?? (() => true);
        _bubble = bubble;
        _glow = glow;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _store.Changed += OnPrefsChanged;
    }

    private void StampAgent() => Interlocked.Exchange(ref _lastAgentTicks, _now().UtcTicks);

    private string ActiveTone(PresencePreferences prefs)
        => (PresenceMode)_mode == PresenceMode.Observing ? PresenceTones.Observing : prefs.Tone;

    private bool Active
    {
        get
        {
            var p = _store.Current;
            if (!p.IsCursorActive) return false;
            if (p.SuppressWhenSessionDisconnected && !_isSessionInteractive()) return false;
            return true;
        }
    }

    /// <summary>
    /// Current activity-derived mode for other local, PHI-free presence
    /// surfaces such as the pharmacist panda companion.
    /// </summary>
    public PresenceMode CurrentMode => (PresenceMode)_mode;

    public void MoveTo(int x, int y)
    {
        if (!Active) return;
        StampAgent(); EvaluateMode();
        try
        {
            var prefs = _store.Current;
            lock (_lock)
            {
                if (!_placed)
                {
                    _placed = true;
                    _lastX = x; _lastY = y;
                    _renderer.Reticle(x, y, prefs.CursorSizePx, ActiveTone(prefs)); // first appearance, no glide
                    return;
                }
                var (dur, easing) = PresenceMotion.PlanGlide(_lastX, _lastY, x, y, prefs);
                _renderer.Glide(_lastX, _lastY, x, y, dur, easing, ActiveTone(prefs), prefs.CursorSizePx);
                _lastX = x; _lastY = y;
                if (_bubble is not null && _bubbleText is not null)
                {
                    try { _bubble.Reanchor(x, y); } catch { /* visual-only */ }
                }
            }
        }
        catch (Exception ex) { _logger.Debug("presence MoveTo failed ({ExceptionType})", ex.GetType().Name); }
    }

    public void Reticle(int x, int y)
    {
        if (!Active) return;
        StampAgent();
        try
        {
            var prefs = _store.Current;
            _renderer.Reticle(x, y, prefs.CursorSizePx, ActiveTone(prefs));
        }
        catch (Exception ex) { _logger.Debug("presence Reticle failed ({ExceptionType})", ex.GetType().Name); }
    }

    public void Click(int x, int y)
    {
        if (!Active) return;
        StampAgent(); EvaluateMode();
        try { _renderer.ClickPulse(x, y, ActiveTone(_store.Current)); }
        catch (Exception ex) { _logger.Debug("presence Click failed ({ExceptionType})", ex.GetType().Name); }
    }

    /// <summary>Cursor stays where it is (persistent). Nothing to tear down.</summary>
    public void Park() { /* persistent overlay: no-op */ }

    /// <summary>Show a one-line caption for the current action near the cursor. PHI-vets the
    /// label (drops to action-kind only if it trips a PHI pattern). Gated + try-caught.</summary>
    public void Narrate(string actionKind, string? label, string? tone = null)
    {
        if (_bubble is null || !Active) return;
        StampAgent(); EvaluateMode();
        var prefs = _store.Current;
        if (!prefs.BubbleVisible || prefs.BubbleVerbosity == "off") return;
        try
        {
            var safeLabel = label is not null
                && SuavoAgent.Helper.Actuation.PhiPatternGuard.ContainsPotentialPhi(label, out _)
                    ? null : label;
            var text = BubbleText.For(actionKind, safeLabel);
            _bubbleText = text;
            lock (_lock) { _bubble.Show(text, tone ?? ActiveTone(prefs), _lastX, _lastY); }
        }
        catch (Exception ex) { _logger.Debug("presence Narrate failed ({ExceptionType})", ex.GetType().Name); }
    }

    /// <summary>Record human input (cheap, hook-safe: an interlocked timestamp write only).
    /// The Driving→Observing flip happens on the next EvaluateMode tick.</summary>
    public void OnHumanInput() => Interlocked.Exchange(ref _lastHumanTicks, _now().UtcTicks);

    /// <summary>Recompute the mode from activity timestamps and apply it (glow + tone). Called by the
    /// 1s ticker and inline after agent activity. Returns the current mode.</summary>
    public PresenceMode EvaluateMode()
    {
        var agentTicks = Interlocked.Read(ref _lastAgentTicks);
        var humanTicks = Interlocked.Read(ref _lastHumanTicks);
        DateTimeOffset? la = agentTicks == 0 ? null : new DateTimeOffset(agentTicks, TimeSpan.Zero);
        DateTimeOffset? lh = humanTicks == 0 ? null : new DateTimeOffset(humanTicks, TimeSpan.Zero);
        var mode = PresenceModeLogic.Evaluate(la, lh, _now(), DrivingWindow, ObserveWindow);
        if ((int)mode != _mode)
        {
            _mode = (int)mode;
            ApplyGlow(mode);
        }
        return mode;
    }

    private void ApplyGlow(PresenceMode mode)
    {
        if (_glow is null) return;
        var prefs = _store.Current;
        try
        {
            if (!prefs.GlowVisible || mode == PresenceMode.Idle) { _glow.Hide(); return; }
            var tone = mode == PresenceMode.Observing ? PresenceTones.Observing : prefs.Tone;
            _glow.Show(tone, prefs.GlowIntensity);
        }
        catch (Exception ex) { _logger.Debug("presence glow apply failed ({ExceptionType})", ex.GetType().Name); }
    }

    private void OnPrefsChanged(PresencePreferences prefs)
    {
        try
        {
            if (prefs.IsCursorActive) _renderer.Show();
            else { _renderer.Hide(); _bubble?.Hide(); _glow?.Hide(); }
        }
        catch (Exception ex) { _logger.Debug("presence visibility toggle failed ({ExceptionType})", ex.GetType().Name); }
    }
}
