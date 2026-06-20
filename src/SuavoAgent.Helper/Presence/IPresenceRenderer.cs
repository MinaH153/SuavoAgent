namespace SuavoAgent.Helper.Presence;

/// <summary>Persistent presence overlay. The Windows impl owns ONE long-lived
/// layered window and animates only on demand (idle = no repaint). Tests use a fake.</summary>
public interface IPresenceRenderer
{
    void Glide(int fromX, int fromY, int toX, int toY, int durationMs, string easing, string tone, int diameterPx);
    void Reticle(int x, int y, int diameterPx, string tone);
    void ClickPulse(int x, int y, string tone);
    void Hide();
    void Show();
}
