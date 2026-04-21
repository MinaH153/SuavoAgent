using System.Windows.Input;

namespace SuavoAgent.Setup.Gui.ViewModels;

public sealed class ErrorViewModel
{
    public ErrorViewModel(string title, string detail, Action? onRetry, Action onClose)
    {
        Title = title;
        Detail = detail;
        CanRetry = onRetry != null;
        RetryCommand = new RelayCommand(onRetry ?? (() => { }), () => onRetry != null);
        CloseCommand = new RelayCommand(onClose);
    }

    public string Title { get; }
    public string Detail { get; }
    public bool CanRetry { get; }

    public ICommand RetryCommand { get; }
    public ICommand CloseCommand { get; }
}
