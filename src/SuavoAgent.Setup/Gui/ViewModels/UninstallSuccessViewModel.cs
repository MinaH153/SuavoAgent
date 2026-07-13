using System.Windows.Input;

namespace SuavoAgent.Setup.Gui.ViewModels;

/// <summary>
/// Terminal screen after the runtime is removed and compliance evidence retained. The install
/// flow's <see cref="SuccessViewModel"/> is bound to an InstallContext (agent
/// id, paths, SQL summary, dashboard link) that no longer exists once the agent
/// is gone — so the uninstall gets its own minimal success VM with just the
/// confirmation message and a single Finish action.
/// </summary>
internal sealed class UninstallSuccessViewModel
{
    public UninstallSuccessViewModel(
        Action onFinish,
        string headline = "SuavoAgent removed",
        string message = "SuavoAgent runtime removed. Retained compliance evidence remains protected for required retention.")
    {
        Headline = headline;
        Message = message;
        FinishCommand = new RelayCommand(onFinish);
    }

    public string Headline { get; }
    public string Message { get; }

    public ICommand FinishCommand { get; }
}
