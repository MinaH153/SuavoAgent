using System.Collections.ObjectModel;
using System.Windows.Input;
using Avalonia.Media;

namespace SuavoAgent.Setup.Gui.ViewModels;

public enum LogLineKind { Step, Ok, Warn, Fail, Info }

public sealed class LogLine
{
    public LogLine(string text, LogLineKind kind)
    {
        Text = text;
        Kind = kind;
    }
    public string Text { get; }
    public LogLineKind Kind { get; }

    public IBrush Brush => Kind switch
    {
        LogLineKind.Step => new SolidColorBrush(Color.Parse("#2563EB")),
        LogLineKind.Ok => new SolidColorBrush(Color.Parse("#7A9B6E")),
        LogLineKind.Warn => new SolidColorBrush(Color.Parse("#EA580C")),
        LogLineKind.Fail => new SolidColorBrush(Color.Parse("#C95454")),
        _ => new SolidColorBrush(Color.Parse("#A8A196")),
    };
}

public sealed class PhaseItem : ViewModelBase
{
    private PhaseState _state = PhaseState.Pending;
    private int _percent;
    private string _caption = string.Empty;

    public PhaseItem(string title)
    {
        Title = title;
    }

    public string Title { get; }

    public PhaseState State
    {
        get => _state;
        set
        {
            SetField(ref _state, value);
            RaisePropertyChanged(nameof(IsActive));
            RaisePropertyChanged(nameof(IsDone));
            RaisePropertyChanged(nameof(Icon));
            RaisePropertyChanged(nameof(HasCaption));
        }
    }

    public int Percent
    {
        get => _percent;
        set
        {
            if (SetField(ref _percent, value))
                RaisePropertyChanged(nameof(PercentLabel));
        }
    }

    /// <summary>Playful sub-line under the title while the phase runs (the brain
    /// phase rotates through these — "Waking the brain…" etc.).</summary>
    public string Caption
    {
        get => _caption;
        set
        {
            if (SetField(ref _caption, value))
                RaisePropertyChanged(nameof(HasCaption));
        }
    }

    public bool HasCaption => IsActive && !string.IsNullOrEmpty(_caption);
    public string PercentLabel => _percent > 0 ? $"{_percent}%" : string.Empty;

    public bool IsActive => _state == PhaseState.Running;
    public bool IsDone => _state == PhaseState.Done;

    public string Icon => _state switch
    {
        PhaseState.Done => "✓",
        PhaseState.Running => "▸",
        PhaseState.Failed => "✗",
        _ => "·",
    };
}

public enum PhaseState { Pending, Running, Done, Failed }

public sealed class ProgressViewModel : ViewModelBase
{
    private string _activePhase = "Preparing…";
    private bool _cancelRequested;

    public ProgressViewModel(
        Action onCancel,
        IReadOnlyList<string>? phaseTitles = null,
        bool canCancel = true)
    {
        CanCancel = canCancel;
        CancelCommand = new RelayCommand(() =>
        {
            _cancelRequested = true;
            onCancel();
        }, () => CanCancel);

        // Default to the install phases; the uninstall flow supplies its own
        // titles so the same progress view renders either workflow. Order MUST
        // match InstallOrchestrator.Phase (the int cast indexes this list).
        var titles = phaseTitles ?? new[]
        {
            "Download SuavoAgent",
            "Write configuration",
            "Install the SuavoAgent brain",
            "Start Windows services",
            "Verify installation",
        };

        Phases = new ObservableCollection<PhaseItem>();
        foreach (var title in titles)
            Phases.Add(new PhaseItem(title));
    }

    /// <summary>
    /// Rotating captions for the brain phase — a 1.3 GB download deserves some
    /// personality. Deterministically keyed off percent (testable, no timer).
    /// </summary>
    internal static readonly IReadOnlyList<string> BrainCaptions = new[]
    {
        "Waking the brain…",
        "Loading 1.7 billion neurons…",
        "Teaching it pharmacy…",
        "Calibrating synapses…",
        "Practicing its bedside manner…",
        "Almost sentient (the good kind)…",
    };

    internal static string BrainCaptionFor(int percent) =>
        BrainCaptions[Math.Clamp(percent, 0, 99) * BrainCaptions.Count / 100];

    public ObservableCollection<PhaseItem> Phases { get; }
    public ObservableCollection<LogLine> LogLines { get; } = new();

    /// <summary>Header shown above the phase list. Install vs uninstall set this.</summary>
    public string Title { get; init; } = "Installing SuavoAgent";

    /// <summary>Footer hint next to the Cancel button.</summary>
    public string CancelHint { get; init; } =
        "Cancel aborts before services start. Already-downloaded binaries stay on disk until retry.";

    public string ActivePhase
    {
        get => _activePhase;
        set => SetField(ref _activePhase, value);
    }

    public bool CancelRequested => _cancelRequested;
    public bool CanCancel { get; }

    public ICommand CancelCommand { get; }

    public void AppendLog(string text, LogLineKind kind)
    {
        // Defense in depth: ConsoleUI already scrubs its shared reporter input,
        // but the view model must remain safe if another native UI path appends
        // directly in the future.
        LogLines.Add(new LogLine(SetupLog.SanitizeForLog(text), kind));
        if (LogLines.Count > 500) LogLines.RemoveAt(0);
    }

    public void MarkPhase(int index, PhaseState state)
    {
        if (index < 0 || index >= Phases.Count) return;
        Phases[index].State = state;
        if (state == PhaseState.Running)
            ActivePhase = Phases[index].Title;
    }

    public void UpdatePhaseProgress(string label, int percent)
    {
        // Match by label prefix so "Downloading SuavoAgent.Core.exe" updates phase 0.
        foreach (var phase in Phases)
        {
            if (!phase.IsActive) continue;
            phase.Percent = percent;
            // The brain phase gets its rotating personality line.
            if (phase.Title.Contains("brain", StringComparison.OrdinalIgnoreCase))
                phase.Caption = BrainCaptionFor(percent);
            return;
        }
    }
}
