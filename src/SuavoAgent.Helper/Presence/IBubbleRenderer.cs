namespace SuavoAgent.Helper.Presence;

/// <summary>Cursor-anchored text bubble. Win32 impl owns one layered window;
/// idle = no repaint. Tests use a fake.</summary>
public interface IBubbleRenderer
{
    void Show(string text, string tone, int x, int y);
    void Reanchor(int x, int y);
    void Hide();
}
