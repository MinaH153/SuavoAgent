using System.Windows.Input;
using SuavoAgent.Setup.Gui.Services;

namespace SuavoAgent.Setup.Gui.ViewModels;

/// <summary>
/// Destructive-action confirmation gate before the uninstall runs. Mirrors the
/// Destination step's role in the install flow (the last actionable screen
/// before Progress), but its job is to make the operator confirm an
/// irreversible removal rather than to collect input. Confirm proceeds to the
/// uninstall progress step; Cancel returns to Welcome.
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
