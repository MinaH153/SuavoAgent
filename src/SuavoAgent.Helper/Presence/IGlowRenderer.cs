namespace SuavoAgent.Helper.Presence;

/// <summary>Full-screen FSD edge glow. Win32 impl breathes via blend-alpha (no per-frame
/// bitmap re-render). Tests use a fake.</summary>
public interface IGlowRenderer
{
    void Show(string tone, double intensity);
    void Hide();
}
