namespace SuavoAgent.Helper.Companion;

/// <summary>
/// Mirrors the fixed-copy panda state into both the visual pet and the native
/// Shell tray. The tray supplies a keyboard/screen-reader-accessible equivalent
/// for every human control.
/// </summary>
public sealed class CompositePandaCompanionView : IPandaCompanionView
{
    private readonly IReadOnlyList<IPandaCompanionView> _views;
    private int _disposed;

    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? StopRequested;

    public CompositePandaCompanionView(params IPandaCompanionView[] views)
    {
        if (views is null || views.Length == 0 || views.Any(view => view is null))
            throw new ArgumentException("At least one companion view is required.", nameof(views));
        _views = views;
        foreach (var view in _views)
        {
            view.PauseRequested += OnPause;
            view.ResumeRequested += OnResume;
            view.StopRequested += OnStop;
        }
    }

    public void Start()
    {
        foreach (var view in _views) view.Start();
    }

    public void Render(CompanionPresentation presentation)
    {
        foreach (var view in _views) view.Render(presentation);
    }

    private void OnPause() => PauseRequested?.Invoke();
    private void OnResume() => ResumeRequested?.Invoke();
    private void OnStop() => StopRequested?.Invoke();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var view in _views)
        {
            view.PauseRequested -= OnPause;
            view.ResumeRequested -= OnResume;
            view.StopRequested -= OnStop;
            view.Dispose();
        }
    }
}
