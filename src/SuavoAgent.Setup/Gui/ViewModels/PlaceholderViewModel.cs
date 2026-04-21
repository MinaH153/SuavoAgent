namespace SuavoAgent.Setup.Gui.ViewModels;

public sealed class PlaceholderViewModel
{
    public string Message { get; } =
        "System check, HIPAA consent, install destination, progress, and success "
        + "screens are being wired up next.\n\n"
        + "For scripted fleet deployment run:\n"
        + "  SuavoSetup.exe --console --pharmacy-id <id> --api-key <key>";
}
