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
        LogLineKind.Step => new SolidColorBrush(Color.Parse("#D4A24C")),
        LogLineKind.Ok => new SolidColorBrush(Color.Parse("#7A9B6E")),
        LogLineKind.Warn => new SolidColorBrush(Color.Parse("#E8B65C")),
        LogLineKind.Fail => new SolidColorBrush(Color.Parse("#C95454")),
        _ => new SolidColorBrush(Color.Parse("#A8A196")),
    };
}

public sealed class PhaseItem : ViewModelBase
{
    private PhaseState _state = PhaseState.Pending;
    private int _percent;

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
        }
    }

    public int Percent
    {
        get => _percent;
        set => SetField(ref _percent, value);
    }

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

    public ProgressViewModel(Action onCancel)
    {
        CancelCommand = new RelayCommand(() =>
        {
            _cancelRequested = true;
            onCancel();
        });

        Phases = new ObservableCollection<PhaseItem>
        {
            new("Download binaries"),
            new("Write configuration"),
            new("Install Windows services"),
        };
    }

    public ObservableCollection<PhaseItem> Phases { get; }
    public ObservableCollection<LogLine> LogLines { get; } = new();

    public string ActivePhase
    {
        get => _activePhase;
        set => SetField(ref _activePhase, value);
    }

    public bool CancelRequested => _cancelRequested;

    public ICommand CancelCommand { get; }

    public void AppendLog(string text, LogLineKind kind)
    {
        LogLines.Add(new LogLine(text, kind));
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
            return;
        }
    }
}
