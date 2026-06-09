using System.Windows.Input;

namespace SuavoAgent.Setup.Gui.ViewModels;

/// <summary>
/// Terminal "clean machine" screen after a fully-clean uninstall. The install
/// flow's <see cref="SuccessViewModel"/> is bound to an InstallContext (agent
/// id, paths, SQL summary, dashboard link) that no longer exists once the agent
/// is gone — so the uninstall gets its own minimal success VM with just the
/// confirmation message and a single Finish action.
/// </summary>
internal sealed class UninstallSuccessViewModel
{
    public UninstallSuccessViewModel(Action onFinish)
    {
        FinishCommand = new RelayCommand(onFinish);
    }

    public string Headline => "SuavoAgent removed";
    public string Message => "SuavoAgent removed — this computer is clean.";

    public ICommand FinishCommand { get; }
}
