using System.Windows.Input;

namespace SuavoAgent.Setup.Gui.ViewModels;

public sealed class WelcomeViewModel
{
    public WelcomeViewModel(Action onContinue, Action? onUninstall = null)
    {
        ContinueCommand = new RelayCommand(onContinue);
        UninstallCommand = new RelayCommand(onUninstall ?? (() => { }));
    }

    public ICommand ContinueCommand { get; }
    public ICommand UninstallCommand { get; }
}
