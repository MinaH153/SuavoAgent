using System.Windows.Input;

namespace SuavoAgent.Setup.Gui.ViewModels;

/// <summary>
/// Human confirmation gate for the native service-only maintenance action. It never pairs,
/// downloads code, or weakens signed-cohort checks; it only asks the staged
/// native maintenance host to reassert the installed service configuration.
/// </summary>
internal sealed class RepairConfirmViewModel
{
    public RepairConfirmViewModel(Action onConfirm, Action onCancel)
    {
        ConfirmCommand = new RelayCommand(onConfirm);
        CancelCommand = new RelayCommand(onCancel);
    }

    public ICommand ConfirmCommand { get; }
    public ICommand CancelCommand { get; }
}
