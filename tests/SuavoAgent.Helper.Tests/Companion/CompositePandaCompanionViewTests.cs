using SuavoAgent.Helper.Companion;
using Xunit;

namespace SuavoAgent.Helper.Tests.Companion;

public sealed class CompositePandaCompanionViewTests
{
    [Fact]
    public void MirrorsStateAndAccessibleControlEventsAcrossBothViews()
    {
        var visual = new FakeView();
        var accessible = new FakeView();
        using var composite = new CompositePandaCompanionView(visual, accessible);
        var pauses = 0;
        var resumes = 0;
        var stops = 0;
        composite.PauseRequested += () => pauses++;
        composite.ResumeRequested += () => resumes++;
        composite.StopRequested += () => stops++;
        var presentation = new CompanionPresentation(
            CompanionState.Watching,
            "Watching",
            "Watching this workstation.",
            CanPause: true,
            CanResume: false,
            CanStop: true);

        composite.Start();
        composite.Render(presentation);
        visual.RequestPause();
        visual.RequestResume();
        accessible.RequestStop();

        Assert.True(visual.Started);
        Assert.True(accessible.Started);
        Assert.Same(presentation, visual.Last);
        Assert.Same(presentation, accessible.Last);
        Assert.Equal(1, pauses);
        Assert.Equal(1, resumes);
        Assert.Equal(1, stops);
    }

    private sealed class FakeView : IPandaCompanionView
    {
        public event Action? PauseRequested;
        public event Action? ResumeRequested;
        public event Action? StopRequested;
        public bool Started { get; private set; }
        public CompanionPresentation? Last { get; private set; }
        public void Start() => Started = true;
        public void Render(CompanionPresentation presentation) => Last = presentation;
        public void RequestPause() => PauseRequested?.Invoke();
        public void RequestResume() => ResumeRequested?.Invoke();
        public void RequestStop() => StopRequested?.Invoke();
        public void Dispose() { }
    }
}
