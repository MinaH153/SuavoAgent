using System;
using System.Threading;

namespace SuavoAgent.Helper.Presence;

/// <summary>Drives PresenceController.EvaluateMode every second so the mode demotes to
/// Observing/Idle when the agent goes quiet or the human takes over (the takeover stamp
/// arrives off the input hook; this ticker turns it into the visible mode flip).</summary>
public sealed class PresenceModeTicker : IDisposable
{
    private readonly Timer _timer;

    public PresenceModeTicker(PresenceController controller)
        => _timer = new Timer(
            _ => { try { controller.EvaluateMode(); } catch { /* non-fatal */ } },
            null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    public void Dispose() => _timer.Dispose();
}
