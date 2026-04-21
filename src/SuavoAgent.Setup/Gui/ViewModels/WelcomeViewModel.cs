using System.Windows.Input;

namespace SuavoAgent.Setup.Gui.ViewModels;

public sealed class WelcomeViewModel
{
    public WelcomeViewModel(Action onContinue)
    {
        ContinueCommand = new RelayCommand(onContinue);
    }

    public ICommand ContinueCommand { get; }
}
