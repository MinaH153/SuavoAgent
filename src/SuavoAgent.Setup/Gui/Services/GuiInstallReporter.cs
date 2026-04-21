using Avalonia.Threading;
using SuavoAgent.Setup.Gui.ViewModels;

namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// Routes phase-level events from the install services into the progress
/// view-model. Hops to the Avalonia UI thread on every callback so the
/// append is race-free with the render loop.
/// </summary>
internal sealed class GuiInstallReporter : IInstallReporter
{
    private readonly ProgressViewModel _vm;

    public GuiInstallReporter(ProgressViewModel vm)
    {
        _vm = vm;
    }

    public void Step(string message) => Post(() => _vm.AppendLog($"▶ {message}", LogLineKind.Step));
    public void Ok(string message) => Post(() => _vm.AppendLog($"✓ {message}", LogLineKind.Ok));
    public void Warn(string message) => Post(() => _vm.AppendLog($"! {message}", LogLineKind.Warn));
    public void Fail(string message) => Post(() => _vm.AppendLog($"✗ {message}", LogLineKind.Fail));
    public void Info(string message) => Post(() => _vm.AppendLog($"  {message}", LogLineKind.Info));

    public void Progress(string label, long current, long total)
    {
        if (total <= 0) return;
        var pct = (int)(current * 100 / total);
        Post(() => _vm.UpdatePhaseProgress(label, pct));
    }

    private static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}
