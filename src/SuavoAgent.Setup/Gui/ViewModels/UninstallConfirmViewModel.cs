using System.Windows.Input;
using SuavoAgent.Setup.Gui.Services;

namespace SuavoAgent.Setup.Gui.ViewModels;

/// <summary>
/// Destructive-action confirmation gate before the uninstall runs. Mirrors the
/// Destination step's role in the install flow (the last actionable screen
/// before Progress). Local confirmation expresses intent only; the orchestrator
/// still requires an exact cloud-signed removal claim before changing the box.
/// </summary>
internal sealed class UninstallConfirmViewModel
{
    public UninstallConfirmViewModel(Action onConfirm, Action onCancel)
    {
        ConfirmCommand = new RelayCommand(onConfirm);
        CancelCommand = new RelayCommand(onCancel);
    }

    public string InstallPath => UninstallOrchestrator.DefaultInstallDir;
    public string DataPath => UninstallOrchestrator.DefaultDataDir;

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
}
