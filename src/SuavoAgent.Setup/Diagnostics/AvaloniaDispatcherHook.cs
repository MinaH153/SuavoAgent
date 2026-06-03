using Avalonia.Threading;
using SuavoAgent.Diagnostics;

namespace SuavoAgent.Setup.Diagnostics;

/// Avalonia-specific dispatcher hook. Lives in the Setup project (not
/// SuavoAgent.Diagnostics) because Diagnostics is universal — adding an
/// Avalonia dependency would bloat Core/Broker/Helper/Watchdog with
/// Avalonia.Desktop transitives. Only Setup needs the hook.
///
/// Per spec §7 PR 4 file list: <c>AvaloniaDispatcherHook.cs</c> hooks
/// <c>Dispatcher.UIThread.UnhandledException</c> and routes exceptions
/// through Wire BEFORE the dispatcher's default unhandled path runs.
///
/// Installed via <c>BuildAvaloniaApp().AfterSetup(...)</c> in Program.cs
/// so the hook attaches AFTER the dispatcher exists but BEFORE the first
/// XAML control fires user code that could throw.
internal static class AvaloniaDispatcherHook
{
    private static int _installed;

    public static void Install()
    {
        // Idempotent: only register the handler once even if AfterSetup
        // fires multiple times (designer / preview tooling).
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;

        try
        {
            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandled;
        }
        catch
        {
            // Intentional one-shot degradation (Codex chunk 4a MEDIUM):
            // Dispatcher.UIThread is not initialized in design-time / preview
            // tooling (AppBuilder.SetupWithoutStarting). In production
            // runtime, AfterSetup fires AFTER dispatcher init, so this catch
            // is not reached. We reset _installed = 0 so a later explicit
            // Install() call (e.g., if the host explicitly invokes it after
            // dispatcher is ready) can succeed — but NO automatic retry is
            // scheduled. Wire's AppDomain.UnhandledException hook still
            // captures unhandled exceptions for the agent-side crash trail;
            // only dispatcher-specific exception capture is lost in this
            // degraded path.
            _installed = 0;
        }
    }

    private static void OnDispatcherUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Wire.ReportException(
                WireComponent.Setup,
                e.Exception,
                stage: "AvaloniaDispatcher");
        }
        catch
        {
            // Wire itself failed — fall through. The dispatcher's default
            // handler (or absence of Handled=true) propagates the exception
            // and Wire's AppDomain hook gets a second chance.
        }
        // NOTE: e.Handled stays false. Setting it true would suppress the
        // app crash and leave the UI in an inconsistent state. Wire's job
        // is to record the crash, not prevent it.
    }
}
