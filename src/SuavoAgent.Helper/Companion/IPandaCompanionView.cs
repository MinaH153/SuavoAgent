namespace SuavoAgent.Helper.Companion;

/// <summary>Native view boundary, extracted so the state/control path is testable without Win32.</summary>
public interface IPandaCompanionView : IDisposable
{
    event Action? PauseRequested;
    event Action? ResumeRequested;
    event Action? StopRequested;

    void Start();
    void Render(CompanionPresentation presentation);
}
