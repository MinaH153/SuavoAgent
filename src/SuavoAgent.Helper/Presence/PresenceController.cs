using System;
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
    private string? _bubbleText;
    private readonly object _lock = new();
    private bool _placed;
    private int _lastX, _lastY;

    public PresenceController(
        IPresenceRenderer renderer,
        PresencePreferenceStore store,
        ILogger logger,
        Func<bool>? isSessionInteractive = null,
        IBubbleRenderer? bubble = null)
    {
        _renderer = renderer;
        _store = store;
        _logger = logger.ForContext<PresenceController>();
        _isSessionInteractive = isSessionInteractive ?? (() => true);
        _bubble = bubble;
        _store.Changed += OnPrefsChanged;
    }

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

    public void MoveTo(int x, int y)
    {
        if (!Active) return;
        try
        {
            var prefs = _store.Current;
            lock (_lock)
            {
                if (!_placed)
                {
                    _placed = true;
                    _lastX = x; _lastY = y;
                    _renderer.Reticle(x, y, prefs.CursorSizePx, prefs.Tone); // first appearance, no glide
                    return;
                }
                var (dur, easing) = PresenceMotion.PlanGlide(_lastX, _lastY, x, y, prefs);
                _renderer.Glide(_lastX, _lastY, x, y, dur, easing, prefs.Tone, prefs.CursorSizePx);
                _lastX = x; _lastY = y;
                if (_bubble is not null && _bubbleText is not null)
                {
                    try { _bubble.Reanchor(x, y); } catch { /* visual-only */ }
                }
            }
        }
        catch (Exception ex) { _logger.Debug(ex, "presence MoveTo failed (non-fatal)"); }
    }

    public void Reticle(int x, int y)
    {
        if (!Active) return;
        try
        {
            var prefs = _store.Current;
            _renderer.Reticle(x, y, prefs.CursorSizePx, prefs.Tone);
        }
        catch (Exception ex) { _logger.Debug(ex, "presence Reticle failed (non-fatal)"); }
    }

    public void Click(int x, int y)
    {
        if (!Active) return;
        try { _renderer.ClickPulse(x, y, _store.Current.Tone); }
        catch (Exception ex) { _logger.Debug(ex, "presence Click failed (non-fatal)"); }
    }

    /// <summary>Cursor stays where it is (persistent). Nothing to tear down.</summary>
    public void Park() { /* persistent overlay: no-op */ }

    /// <summary>Show a one-line caption for the current action near the cursor. PHI-vets the
    /// label (drops to action-kind only if it trips a PHI pattern). Gated + try-caught.</summary>
    public void Narrate(string actionKind, string? label, string? tone = null)
    {
        if (_bubble is null || !Active) return;
        var prefs = _store.Current;
        if (!prefs.BubbleVisible || prefs.BubbleVerbosity == "off") return;
        try
        {
            var safeLabel = label is not null
                && SuavoAgent.Helper.Actuation.PhiPatternGuard.ContainsPotentialPhi(label, out _)
                    ? null : label;
            var text = BubbleText.For(actionKind, safeLabel);
            _bubbleText = text;
            lock (_lock) { _bubble.Show(text, tone ?? prefs.Tone, _lastX, _lastY); }
        }
        catch (Exception ex) { _logger.Debug(ex, "presence Narrate failed (non-fatal)"); }
    }

    private void OnPrefsChanged(PresencePreferences prefs)
    {
        try
        {
            if (prefs.IsCursorActive) _renderer.Show();
            else { _renderer.Hide(); _bubble?.Hide(); }
        }
        catch (Exception ex) { _logger.Debug(ex, "presence visibility toggle failed (non-fatal)"); }
    }
}
