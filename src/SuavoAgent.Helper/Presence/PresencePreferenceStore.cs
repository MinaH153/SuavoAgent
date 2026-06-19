using System;

namespace SuavoAgent.Helper.Presence;

/// <summary>Holds the live presence preferences. Thread-safe. SetVisible is the
/// instant local-hide override (hotkey/tray/IPC); Replace applies a full pref set
/// (e.g. cloud sync, Phase 5). Raises Changed only on an actual change.</summary>
public sealed class PresencePreferenceStore
{
    private readonly object _lock = new();
    private PresencePreferences _current;

    public PresencePreferenceStore(PresencePreferences initial)
        => _current = initial ?? PresencePreferences.SafeDefault();

    public PresencePreferences Current
    {
        get { lock (_lock) return _current; }
    }

    public event Action<PresencePreferences>? Changed;

    public void SetVisible(bool visible)
    {
        PresencePreferences next;
        lock (_lock)
        {
            if (_current.CursorVisible == visible) return;
            _current = _current with { CursorVisible = visible };
            next = _current;
        }
        Changed?.Invoke(next);
    }

    public void Replace(PresencePreferences prefs)
    {
        if (prefs is null) return;
        lock (_lock) { _current = prefs; }
        Changed?.Invoke(prefs);
    }
}
